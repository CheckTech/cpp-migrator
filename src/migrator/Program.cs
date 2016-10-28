using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using System.Xml.Linq;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Text;

namespace migrator
{
  class Program
  {
    static bool _renameBuildConfigurations = true;

    static void Main(string[] args)
    {
      if (args.Length < 1)
      {
        PrintUsage();
        return;
      }

      string projectFolder = args[0];

      MakeSolutionChanges(projectFolder);
      MakeProjectChanges(projectFolder);
      MakeCodeChanges(projectFolder);
    }

    private static void MakeSolutionChanges(string projectFolder)
    {
      foreach (var file in Directory.GetFiles(projectFolder, "*.sln"))
      {
        StringBuilder body = new StringBuilder();
        using (var f = new StreamReader(file))
        {
          string line;
          while ((line = f.ReadLine()) != null)
          {
            if (CultureInfo.InvariantCulture.CompareInfo.IndexOf(line, "Win32", CompareOptions.IgnoreCase) >= 0)
              continue;

            line = line.Replace("PseudoDebug", "DebugRhino");
            body.Append(line).Append("\r\n");
          }
        }

        File.WriteAllText(file, body.ToString());
      }
    }

    static void MakeProjectChanges(string projectFolder)
    {
      // Update .vcxproj files
      string ns = "{http://schemas.microsoft.com/developer/msbuild/2003}";
      foreach (var file in Directory.GetFiles(projectFolder, "*.vcxproj"))
      {
        XDocument vcxproj = XDocument.Load(file);

        // Update tools version
        if (vcxproj.Root != null)
        {
          vcxproj.Root.SetAttributeValue("ToolsVersion", "14.0");

          // Update Project Configurations

          // First, see if the DebugRhino already exists, and don't do this twice.
          var existingConfiguration = vcxproj.Root.Descendants(ns + "ProjectConfiguration").FirstOrDefault(el => el.Attribute("Include")?.Value == "DebugRhino|x64");
          if (existingConfiguration != null)
            _renameBuildConfigurations = false;

          var elementsToRemove = new List<XElement>();
          foreach (XElement projectConfiguration in vcxproj.Root.Elements(ns + "ItemGroup").Elements(ns + "ProjectConfiguration"))
          {
            var xAttribute = projectConfiguration.Attribute("Include");
            if (xAttribute != null)
            {
              var include = xAttribute.Value;
              if (include.Contains("Win32"))
              {
                // Remove Win32 Project Configurations
                elementsToRemove.Add(projectConfiguration);
              }

              if (_renameBuildConfigurations && include.StartsWith("Debug|x64"))
              {
                // Rename Debug project configurations to DebugRhino
                var config = projectConfiguration.Element(ns + "Configuration");
                projectConfiguration.SetAttributeValue("Include", "DebugRhino|x64");
                if (config != null) config.Value = "DebugRhino";
              }

              if (include.StartsWith("PseudoDebug|x64"))
              {
                // Rename PseudoDebug project configurations to Debug
                var config = projectConfiguration.Element(ns + "Configuration");
                projectConfiguration.SetAttributeValue("Include", "Debug|x64");
                if (config != null) config.Value = "Debug";
              }
            }
          }


          // Update Build Settings
          foreach (XElement propertyGroup in vcxproj.Root.Elements(ns + "PropertyGroup"))
          {
            if (CleanupElementCondition(propertyGroup))
              elementsToRemove.Add(propertyGroup);

            var propertyGroupLabel = propertyGroup.Attribute("Label")?.Value;
            var platformToolset = propertyGroup.Element(ns + "PlatformToolset");
            if (platformToolset == null && propertyGroupLabel != null && propertyGroupLabel == "Configuration")
            {
              platformToolset = new XElement(ns + "PlatformToolset");
              propertyGroup.Add(platformToolset);
            }

            if (platformToolset != null)
              platformToolset.Value = "v140";
          }

          foreach (XElement itemDefinitionGroup in vcxproj.Root.Elements(ns + "ItemDefinitionGroup"))
          {
            if (CleanupElementCondition(itemDefinitionGroup))
              elementsToRemove.Add(itemDefinitionGroup);
          }

          // Precompiled Header settings
          foreach (XElement itemDefinitionGroup in vcxproj.Root.Elements(ns + "ItemGroup").Elements(ns + "ClCompile"))
          {
            var includeAttribute = itemDefinitionGroup.Attribute("Include");
            if (includeAttribute == null)
              continue;

            var include = includeAttribute.Value;

            if (include == "stdafx.cpp")
            {
              var precompiledHeaderElements = itemDefinitionGroup.Elements(ns + "PrecompiledHeader");
              foreach (var element in precompiledHeaderElements)
              {
                if (CleanupElementCondition(element))
                  elementsToRemove.Add(element);
              }
            }
          }

          // Add targetver.h file 
          var result = vcxproj.Root.Descendants(ns + "ClInclude").FirstOrDefault(el => el.Attribute("Include")?.Value == "targetver.h");
          if (result == null)
          {
            result = vcxproj.Root.Descendants(ns + "ClInclude").FirstOrDefault(el => el.Attribute("Include")?.Value == "stdafx.h");
            result?.Parent?.Add(new XElement(ns + "ClInclude", new XAttribute("Include", "targetver.h")));
          }

          // Add Macro to find SDK
          // Does it exist?
          var existingSdkPathMacro = vcxproj.Root.Descendants(ns + "RhinoSdkPath");
          if (!existingSdkPathMacro.Any())
          {
            // Nope. Add the macro.
            var macroElement = new XElement(ns + "PropertyGroup");
            var sdkVersionElement = new XElement(ns + "RhinoSdkVersion") {Value = "6.0"};
            var sdkPathElement = new XElement(ns + "RhinoSdkPath");
            sdkPathElement.Value = @"$([MSBuild]::GetRegistryValueFromView('HKEY_LOCAL_MACHINE\SOFTWARE\McNeel\Rhinoceros\SDK\$(RhinoSdkVersion)', 'InstallPath', null, RegistryView.Registry64))";
            macroElement.Add(sdkVersionElement);
            macroElement.Add(sdkPathElement);
            vcxproj.Root.AddFirst(macroElement);

            // Add property sheets
            foreach (var propertySheetElement in vcxproj.Root.Descendants(ns + "ImportGroup").Where(el => el.Attribute("Label")?.Value == "PropertySheets"))
            {
              if (CleanupElementCondition(propertySheetElement))
              {
                elementsToRemove.Add(propertySheetElement);
                continue;
              }
              result = propertySheetElement.Descendants("Import").FirstOrDefault(el => (el.Attribute("Project")?.Value).EndsWith("Rhino.Cpp.PlugIn.props"));
              if (result == null) // No Rhino Property Sheets here
              {
                propertySheetElement.Add(new XElement(ns + "Import", new XAttribute("Project", "$(RhinoSdkPath)PropertySheets\\Rhino.Cpp.PlugIn.props")));
              }
            }
          }

          // Remove unwanted elements
          foreach (var element in elementsToRemove)
            element.Remove();
        }

        vcxproj.Save(file);
        }
    }

    static bool CleanupElementCondition(XElement element)
    {
      var conditionAttribute = element.Attribute("Condition");
      if (conditionAttribute == null)
        return false;

      var condition = conditionAttribute.Value;
      if (condition.Contains("'$(Configuration)|$(Platform)'=='Debug|Win32'"))
        return true;
      if (condition.Contains("'$(Configuration)|$(Platform)'=='Release|Win32'"))
        return true;
      if (condition.Contains("'$(Configuration)|$(Platform)'=='PseudoDebug|Win32'"))
        return true;

      if (_renameBuildConfigurations && condition.Contains("'$(Configuration)|$(Platform)'=='Debug|x64'"))
        element.SetAttributeValue("Condition", "'$(Configuration)|$(Platform)'=='DebugRhino|x64'");
      if (_renameBuildConfigurations && condition.Contains("'$(Configuration)|$(Platform)'=='PseudoDebug|x64'"))
        element.SetAttributeValue("Condition", "'$(Configuration)|$(Platform)'=='Debug|x64'");

      return false; 
   }

    private static void MakeCodeChanges(string projectFolder)
    {
      string json = File.ReadAllText(Path.Combine(AssemblyDirectory, "replacements.json"));
      Dictionary<string, string> replacements = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);

      foreach (var file in Directory.GetFiles(projectFolder, "*.cpp"))
      {
        ReplaceInFile(file, replacements);
      }
      foreach (var file in Directory.GetFiles(projectFolder, "*.h"))
      {
        ReplaceInFile(file, replacements);
      }

      // stdafx.h
      var stdafxHPath = Path.Combine(projectFolder, "stdafx.h");
      var stdafxHReplacements = new Dictionary<string, string>()
      {
        {"#pragma once", "#pragma once\r\n#define RHINO_V6_READY"}
      };
      ReplaceInFile(stdafxHPath, stdafxHReplacements);

      // Create targetver.h if it doesn't exist
      var targetverHPath = Path.Combine(projectFolder, "targetver.h");
      if (!File.Exists(targetverHPath))
      {
        File.Copy(Path.Combine(AssemblyDirectory, "targetver.h"), targetverHPath);
      }
    }

    private static void ReplaceInFile(string filename, Dictionary<string, string> replacements)
    {
      // Read contents of file into string
      var body = File.ReadAllText(filename);
      var originalBody = body;

      foreach (var key in replacements.Keys)
      {
        var rx = new Regex(key);
        body = rx.Replace(body, replacements[key]);
      }

      if (body != originalBody)
        File.WriteAllText(filename, body);
    }

    private static void PrintUsage()
    {
      Console.WriteLine("migrator.exe <path_to_folder>");
    }

    public static string AssemblyDirectory
    {
      get
      {
        string codeBase = Assembly.GetExecutingAssembly().CodeBase;
        UriBuilder uri = new UriBuilder(codeBase);
        string path = Uri.UnescapeDataString(uri.Path);
        return Path.GetDirectoryName(path);
      }
    }
  }
}
