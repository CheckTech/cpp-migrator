# cpp-migrator
A tool to migrate your C++ Rhino Plug-in from Rhino 5 SDK to Rhino 6 SDK

## Usage
1. Download and extract the latest release.
1. Run migrator.exe [plugin-dir]

## What happens?
1. Your .vcxproj file is updated to use Rhino's Project Property Sheets
1. Your .vcxproj file is updated to include "targetver.h", if it doesn't already
1. Build configurations in your .vcxproj file are update by renaming "Debug" to "DebugRhino" and "PseudoDebug" to "Debug".
1. Your .cpp and .h files are modified by matching V5 SDK patterns with new V6 SDK patterns.
1. A number of changes cannot be automatically migrated, and in those cases a more descriptive #error is inserted.

Not every possible source code change is made, but the ones we think will be most common are. Additional replacements can be made by request.

Running this tool multiple times on the same project *should work fine*, but it's probably safer to rerun the tool on an unmodified V5 project.
