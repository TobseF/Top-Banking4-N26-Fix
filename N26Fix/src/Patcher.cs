// Patcher: haengt N26Throttle.GetJson in Subsembly.Scraper.dll ein.
// Aufruf: Patcher.exe <Original Subsembly.Scraper.dll> <Ausgabe-DLL> <Subsembly.Scraper.N26Fix.dll> <Banking4-Ordner>
using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

class Patcher
{
    static int Main(string[] args)
    {
        if (args.Length != 4)
        {
            Console.Error.WriteLine("Usage: Patcher <in.dll> <out.dll> <fix.dll> <appdir>");
            return 2;
        }

        var aResolver = new DefaultAssemblyResolver();
        aResolver.AddSearchDirectory(args[3]);
        aResolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2])));

        var aParams = new ReaderParameters { AssemblyResolver = aResolver, ReadingMode = ReadingMode.Immediate };
        var aMod = ModuleDefinition.ReadModule(args[0], aParams);
        var aFixMod = ModuleDefinition.ReadModule(args[2], aParams);

        if (aMod.AssemblyReferences.Any(r => r.Name == aFixMod.Assembly.Name.Name))
        {
            Console.Error.WriteLine("Bereits gepatcht.");
            return 3;
        }

        var aFixType = aFixMod.GetType("Subsembly.Scraper.N26Fix.N26Throttle");
        var aFixMethod = aFixType.Methods.Single(m => m.Name == "GetJson");
        var aImported = aMod.ImportReference(aFixMethod);

        var aN26 = aMod.GetType("Subsembly.Scraper.ScraperSessionN26");
        int nPatched = 0;
        foreach (var m in aN26.Methods.Concat(aN26.NestedTypes.SelectMany(t => t.Methods)))
        {
            if (!m.HasBody) continue;
            foreach (var ins in m.Body.Instructions)
            {
                var mr = ins.Operand as MethodReference;
                if ((ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt) && mr != null
                    && mr.Name == "HttpGetJson" && mr.DeclaringType.FullName == "Subsembly.Interweb.HttpClient"
                    && mr.Parameters.Count == 3)
                {
                    ins.OpCode = OpCodes.Call;
                    ins.Operand = aImported;
                    ++nPatched;
                    Console.WriteLine("  gepatcht: " + m.Name + " @ IL_" + ins.Offset.ToString("x4"));
                }
            }
        }

        if (nPatched == 0)
        {
            Console.Error.WriteLine("Keine Aufrufstellen gefunden - Version passt nicht?");
            return 4;
        }

        aMod.Write(args[1]);
        Console.WriteLine(nPatched + " Aufrufe umgeleitet -> " + args[1]);
        return 0;
    }
}
