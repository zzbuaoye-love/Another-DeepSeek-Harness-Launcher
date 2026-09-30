using AnotherDSHL.Installer.Core;
if (args.Length != 3) throw new ArgumentException("Usage: PackageTool <baseline-payload> <new-payload> <output.adup>");
UpdatePackageService.Create(args[0], args[1], args[2]);
Console.WriteLine(args[2]);
