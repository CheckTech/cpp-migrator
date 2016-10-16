using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace migrator
{
  class Program
  {
    static void Main(string[] args)
    {
      if (args.Length < 1)
      {
        PrintUsage();
        return;
      }

      string projectFolder = args[0];

      string json = System.IO.File.ReadAllText("replacements.json");
      Dictionary<string, string> replacements = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);

      foreach (var file in System.IO.Directory.GetFiles(projectFolder, "*.cpp"))
      {
        ReplaceInFile(file, replacements);
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
  }
}
