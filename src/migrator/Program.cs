using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using System.Xml.Linq;
using System.Linq;
using System.Reflection;
using System.IO;

namespace migrator
{
  class Program
  {
    static bool rename_build_configurations = true;

    static void Main(string[] args)
    {
      if (args.Length < 1)
      {
        PrintUsage();
        return;
      }

      string projectFolder = args[0];

      MakeProjectChanges(projectFolder);
      MakeCodeChanges(projectFolder);
    }

    static void MakeProjectChanges(string projectFolder)
    {
      // Update .vcxproj files
      foreach (var file in System.IO.Directory.GetFiles(projectFolder, "*.vcxproj"))
      {
        XDocument vcxproj = XDocument.Load(file);
        string ns = "{http://schemas.microsoft.com/developer/msbuild/2003}";

        // Update tools version
        vcxproj.Root.SetAttributeValue("ToolsVersion", "14.0");

        // Update Project Configurations

        // First, see if the DebugRhino already exists, and don't do this twice.
        var existing_configuration = vcxproj.Root.Descendants(ns + "ProjectConfiguration").FirstOrDefault(el => el.Attribute("Include")?.Value == "DebugRhino|x64");
        if (existing_configuration != null)
          rename_build_configurations = false;

        var elements_to_remove = new List<XElement>();
        foreach (XElement project_configuration in vcxproj.Root.Elements(ns + "ItemGroup").Elements(ns + "ProjectConfiguration"))
        {
          var include = project_configuration.Attribute("Include").Value;
          if (include.Contains("Win32"))
          {
            // Remove Win32 Project Configurations
            elements_to_remove.Add(project_configuration);
          }

          if (rename_build_configurations && include.StartsWith("Debug|x64"))
          {
            // Rename Debug project configurations to DebugRhino
            var config = project_configuration.Element(ns + "Configuration");
            project_configuration.SetAttributeValue("Include", "DebugRhino|x64");
            config.Value = "DebugRhino";
          }

          if (include.StartsWith("PseudoDebug|x64"))
          {
            // Rename PseudoDebug project configurations to Debug
            var config = project_configuration.Element(ns + "Configuration");
            project_configuration.SetAttributeValue("Include", "Debug|x64");
            config.Value = "Debug";
          }
        }


        // Update Build Settings
        foreach (XElement property_group in vcxproj.Root.Elements(ns + "PropertyGroup"))
        {
          if (CleanupElementCondition(property_group))
            elements_to_remove.Add(property_group);

          var platform_toolset = property_group.Element(ns + "PlatformToolset");
          if (platform_toolset != null)
            platform_toolset.Value = "v140";
        }

        foreach (XElement item_definition_group in vcxproj.Root.Elements(ns + "ItemDefinitionGroup"))
        {
          if (CleanupElementCondition(item_definition_group))
            elements_to_remove.Add(item_definition_group);
        }

        // Precompiled Header settings
        foreach (XElement item_definition_group in vcxproj.Root.Elements(ns + "ItemGroup").Elements(ns + "ClCompile"))
        {
          var include_attribute = item_definition_group.Attribute("Include");
          if (include_attribute == null)
            continue;

          var include = include_attribute.Value;

          if (include == "stdafx.cpp")
          {
            var precompiled_header_elements = item_definition_group.Elements(ns + "PrecompiledHeader");
            foreach (var element in precompiled_header_elements)
            {
              if (CleanupElementCondition(element))
                elements_to_remove.Add(element);
            }
          }
        }

        // Add targetver.h file 
        var result = vcxproj.Root.Descendants(ns + "ClInclude").FirstOrDefault(el => el.Attribute("Include")?.Value == "targetver.h");
        if (result == null)
        {
          result = vcxproj.Root.Descendants(ns + "ClInclude").FirstOrDefault(el => el.Attribute("Include")?.Value == "stdafx.h");
          result.Parent.Add(new XElement(ns + "ClInclude", new XAttribute("Include", "targetver.h")));
        }

        // Add property sheets
        foreach (var property_sheet_element in vcxproj.Root.Descendants(ns + "ImportGroup").Where(el => el.Attribute("Label")?.Value == "PropertySheets"))
        {
          if (CleanupElementCondition(property_sheet_element))
          {
            elements_to_remove.Add(property_sheet_element);
            continue;
          }
          result = property_sheet_element.Descendants("Import").FirstOrDefault(el => ((string)el.Attribute("Project")?.Value).EndsWith("Rhino.Cpp.PlugIn.props"));
          if (result == null) // No Rhino Property Sheets here
          {
            property_sheet_element.Add(new XElement(ns + "Import", new XAttribute("Project", "$(ProgramW6432)\\Rhino 6.0 SDK\\PropertySheets\\Rhino.Cpp.PlugIn.props")));
          }
        }



        // Remove unwanted elements
        foreach (var element in elements_to_remove)
          element.Remove();

        vcxproj.Save(file);
        }
    }

    static bool CleanupElementCondition(XElement element)
    {
      var condition_attribute = element.Attribute("Condition");
      if (condition_attribute == null)
        return false;

      var condition = condition_attribute.Value;
      if (condition.Contains("'$(Configuration)|$(Platform)'=='Debug|Win32'"))
        return true;
      if (condition.Contains("'$(Configuration)|$(Platform)'=='Release|Win32'"))
        return true;
      if (condition.Contains("'$(Configuration)|$(Platform)'=='PseudoDebug|Win32'"))
        return true;

      if (rename_build_configurations && condition.Contains("'$(Configuration)|$(Platform)'=='Debug|x64'"))
        element.SetAttributeValue("Condition", "'$(Configuration)|$(Platform)'=='DebugRhino|x64'");
      if (rename_build_configurations && condition.Contains("'$(Configuration)|$(Platform)'=='PseudoDebug|x64'"))
        element.SetAttributeValue("Condition", "'$(Configuration)|$(Platform)'=='Debug|x64'");

      return false; 
   }

    static void MakeCodeChanges(string projectFolder)
    {
      string json = System.IO.File.ReadAllText(Path.Combine(AssemblyDirectory, "replacements.json"));
      Dictionary<string, string> replacements = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);

      foreach (var file in System.IO.Directory.GetFiles(projectFolder, "*.cpp"))
      {
        ReplaceInFile(file, replacements);
      }
      foreach (var file in System.IO.Directory.GetFiles(projectFolder, "*.h"))
      {
        ReplaceInFile(file, replacements);
      }

      // stdafx.h
      var stdafx_h_path = System.IO.Path.Combine(projectFolder, "stdafx.h");
      var stdafx_h_replacements = new Dictionary<string, string>()
      {
        {"#pragma once", "#pragma once\r\n#define RHINO_V6_READY"}
      };
      ReplaceInFile(stdafx_h_path, stdafx_h_replacements);

      // Create targetver.h if it doesn't exist
      var targetver_h_path = Path.Combine(projectFolder, "targetver.h");
      if (!File.Exists(targetver_h_path))
      {
        File.Copy(Path.Combine(AssemblyDirectory, "targetver.h"), targetver_h_path);
      }
    }

    static void ReplaceInFile(string filename, Dictionary<string, string> replacements)
    {
      // Read contents of file into string
      var body = System.IO.File.ReadAllText(filename);
      var original_body = body;

      foreach (var key in replacements.Keys)
      {
        var rx = new Regex(key);
        body = rx.Replace(body, replacements[key]);
      }

      if (body != original_body)
        System.IO.File.WriteAllText(filename, body);
    }

    static void PrintUsage()
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
