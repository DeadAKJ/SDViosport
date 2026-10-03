using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;

class Program
{
    static int Main(string[] args)
    {
        string targetDll = args.Length > 0 ? args[0] : @"C:\Users\User\Desktop\SDVport Version\Game_Files\smapi-internal\0Harmony.dll";
        if (!File.Exists(targetDll))
        {
            Console.WriteLine($"Error: File not found: {targetDll}");
            return 1;
        }

        Console.WriteLine($"Patching 0Harmony at: {targetDll}");

        var readerParams = new ReaderParameters { ReadWrite = true };
        var asm = AssemblyDefinition.ReadAssembly(targetDll, readerParams);
        var mod = asm.MainModule;

        // 1. Inject HarmonySharedState type into 0Harmony.dll if not present
        var existingShared = mod.GetType("HarmonySharedState");
        if (existingShared == null)
        {
            Console.WriteLine("  Injecting HarmonySharedState class into 0Harmony.dll...");
            var sharedType = new TypeDefinition(
                "",
                "HarmonySharedState",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Abstract | Mono.Cecil.TypeAttributes.Sealed,
                mod.TypeSystem.Object
            );
            mod.Types.Add(sharedType);

            // Field: version (int)
            var versionField = new FieldDefinition(
                "version",
                Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static,
                mod.TypeSystem.Int32
            );
            sharedType.Fields.Add(versionField);

            // Field: state (Dictionary<MethodBase, byte[]>)
            var stateTypeRef = mod.ImportReference(typeof(Dictionary<MethodBase, byte[]>));
            var stateField = new FieldDefinition(
                "state",
                Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static,
                stateTypeRef
            );
            sharedType.Fields.Add(stateField);

            // Field: originals (Dictionary<MethodInfo, MethodBase>)
            var origTypeRef = mod.ImportReference(typeof(Dictionary<MethodInfo, MethodBase>));
            var origField = new FieldDefinition(
                "originals",
                Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static,
                origTypeRef
            );
            sharedType.Fields.Add(origField);
        }
        else
        {
            Console.WriteLine("  HarmonySharedState class already present.");
        }

        var t = mod.GetType("HarmonyLib.HarmonySharedState");
        if (t != null)
        {
            // 2. Neutralize RefreshMethodStarts
            var refresh = t.Methods.FirstOrDefault(m => m.Name == "RefreshMethodStarts");
            if (refresh != null)
            {
                Console.WriteLine("  Neutralizing RefreshMethodStarts with ret...");
                refresh.Body.Instructions.Clear();
                refresh.Body.Variables.Clear();
                refresh.Body.ExceptionHandlers.Clear();
                refresh.Body.GetILProcessor().Emit(OpCodes.Ret);
            }

            var cctor = t.Methods.FirstOrDefault(m => m.Name == ".cctor");
            if (cctor != null)
            {
                for (int i = 0; i < cctor.Body.Instructions.Count; i++)
                {
                    var ins = cctor.Body.Instructions[i];
                    if (ins.OpCode == OpCodes.Call && ins.Operand?.ToString()?.Contains("RefreshMethodStarts") == true)
                    {
                        Console.WriteLine($"  Neutralizing RefreshMethodStarts call in .cctor at offset {ins.Offset:X4}...");
                        ins.OpCode = OpCodes.Nop;
                        ins.Operand = null;
                    }
                }
            }

            // 3. Patch GetOrCreateSharedStateType to directly return typeof(HarmonySharedState)
            var getOrCreate = t.Methods.FirstOrDefault(m => m.Name == "GetOrCreateSharedStateType");
            if (getOrCreate != null)
            {
                Console.WriteLine("  Patching GetOrCreateSharedStateType to directly return HarmonySharedState...");
                var getOrCreateIL = getOrCreate.Body.GetILProcessor();
                getOrCreate.Body.Instructions.Clear();
                getOrCreate.Body.Variables.Clear();
                getOrCreate.Body.ExceptionHandlers.Clear();
                var sharedTypeDef = mod.GetType("HarmonySharedState");
                getOrCreateIL.Append(Instruction.Create(OpCodes.Ldtoken, sharedTypeDef));
                var getTypeFromHandle = mod.ImportReference(typeof(Type).GetMethod("GetTypeFromHandle", new[] { typeof(RuntimeTypeHandle) }));
                getOrCreateIL.Append(Instruction.Create(OpCodes.Call, getTypeFromHandle));
                getOrCreateIL.Append(Instruction.Create(OpCodes.Ret));
            }
        }

        var pp = mod.GetType("HarmonyLib.PatchProcessor");
        if (pp != null)
        {
            var patchMethod = pp.Methods.FirstOrDefault(m => m.Name == "Patch" && m.Parameters.Count == 0);
            var origField = pp.Fields.FirstOrDefault(f => f.Name == "original");
            if (patchMethod != null && origField != null)
            {
                Console.WriteLine("  Neutralizing PatchProcessor.Patch() for iOS W^X safety...");
                patchMethod.Body.Instructions.Clear();
                patchMethod.Body.Variables.Clear();
                patchMethod.Body.ExceptionHandlers.Clear();
                var il = patchMethod.Body.GetILProcessor();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, origField);
                var methodInfoRef = mod.ImportReference(typeof(MethodInfo));
                il.Emit(OpCodes.Isinst, methodInfoRef);
                il.Emit(OpCodes.Ret);
            }
        }

        // 4. Neutralize PatchClassProcessor.Patch() and ProcessPatchJob
        var pcp = mod.GetType("HarmonyLib.PatchClassProcessor");
        if (pcp != null)
        {
            var pcpPatch = pcp.Methods.FirstOrDefault(m => m.Name == "Patch" && m.Parameters.Count == 0);
            if (pcpPatch != null)
            {
                Console.WriteLine("  Neutralizing PatchClassProcessor.Patch() -> returns empty List<MethodInfo>...");
                pcpPatch.Body.Instructions.Clear();
                pcpPatch.Body.Variables.Clear();
                pcpPatch.Body.ExceptionHandlers.Clear();
                var il = pcpPatch.Body.GetILProcessor();
                var listCtor = mod.ImportReference(typeof(List<MethodInfo>).GetConstructor(Type.EmptyTypes));
                il.Emit(OpCodes.Newobj, listCtor);
                il.Emit(OpCodes.Ret);
            }

            var pcpJob = pcp.Methods.FirstOrDefault(m => m.Name == "ProcessPatchJob");
            if (pcpJob != null)
            {
                Console.WriteLine("  Neutralizing PatchClassProcessor.ProcessPatchJob() -> ret...");
                pcpJob.Body.Instructions.Clear();
                pcpJob.Body.Variables.Clear();
                pcpJob.Body.ExceptionHandlers.Clear();
                pcpJob.Body.GetILProcessor().Emit(OpCodes.Ret);
            }
        }

        // 5. Neutralize PatchFunctions.UpdateWrapper
        var pf = mod.GetType("HarmonyLib.PatchFunctions");
        if (pf != null)
        {
            var updateWrapper = pf.Methods.FirstOrDefault(m => m.Name == "UpdateWrapper");
            if (updateWrapper != null)
            {
                Console.WriteLine("  Neutralizing PatchFunctions.UpdateWrapper() -> returns original as MethodInfo...");
                updateWrapper.Body.Instructions.Clear();
                updateWrapper.Body.Variables.Clear();
                updateWrapper.Body.ExceptionHandlers.Clear();
                var il = updateWrapper.Body.GetILProcessor();
                il.Emit(OpCodes.Ldarg_0);
                var methodInfoRef = mod.ImportReference(typeof(MethodInfo));
                il.Emit(OpCodes.Isinst, methodInfoRef);
                il.Emit(OpCodes.Ret);
            }
        }

        // 6. Neutralize HarmonyLib.Memory native detour operations
        var mem = mod.GetType("HarmonyLib.Memory");
        if (mem != null)
        {
            var writeJump = mem.Methods.FirstOrDefault(m => m.Name == "WriteJump");
            if (writeJump != null)
            {
                Console.WriteLine("  Neutralizing Memory.WriteJump() -> returns null...");
                writeJump.Body.Instructions.Clear();
                writeJump.Body.Variables.Clear();
                writeJump.Body.ExceptionHandlers.Clear();
                var il = writeJump.Body.GetILProcessor();
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Ret);
            }

            var detourMethod = mem.Methods.FirstOrDefault(m => m.Name == "DetourMethod");
            if (detourMethod != null)
            {
                Console.WriteLine("  Neutralizing Memory.DetourMethod() -> returns null...");
                detourMethod.Body.Instructions.Clear();
                detourMethod.Body.Variables.Clear();
                detourMethod.Body.ExceptionHandlers.Clear();
                var il = detourMethod.Body.GetILProcessor();
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Ret);
            }

            var detourPersist = mem.Methods.FirstOrDefault(m => m.Name == "DetourMethodAndPersist");
            if (detourPersist != null)
            {
                Console.WriteLine("  Neutralizing Memory.DetourMethodAndPersist() -> ret...");
                detourPersist.Body.Instructions.Clear();
                detourPersist.Body.Variables.Clear();
                detourPersist.Body.ExceptionHandlers.Clear();
                detourPersist.Body.GetILProcessor().Emit(OpCodes.Ret);
            }

            var getMethodStart = mem.Methods.FirstOrDefault(m => m.Name == "GetMethodStart");
            if (getMethodStart != null)
            {
                Console.WriteLine("  Neutralizing Memory.GetMethodStart() -> returns 0L...");
                getMethodStart.Body.Instructions.Clear();
                getMethodStart.Body.Variables.Clear();
                getMethodStart.Body.ExceptionHandlers.Clear();
                var il = getMethodStart.Body.GetILProcessor();
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Stind_Ref);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Conv_I8);
                il.Emit(OpCodes.Ret);
            }
        }

        // 7. Neutralize HarmonySharedState.UpdatePatchInfo
        if (t != null)
        {
            var updateInfo = t.Methods.FirstOrDefault(m => m.Name == "UpdatePatchInfo");
            if (updateInfo != null)
            {
                Console.WriteLine("  Neutralizing HarmonySharedState.UpdatePatchInfo() -> ret...");
                updateInfo.Body.Instructions.Clear();
                updateInfo.Body.Variables.Clear();
                updateInfo.Body.ExceptionHandlers.Clear();
                updateInfo.Body.GetILProcessor().Emit(OpCodes.Ret);
            }
        }

        asm.Write();
        asm.Dispose();
        Console.WriteLine("=== Successfully patched 0Harmony.dll for iOS compatibility! ===");

        try
        {
            Console.WriteLine("Verifying patched assembly via Assembly.Load...");
            var rawBytes = File.ReadAllBytes(targetDll);
            var loadedAsm = Assembly.Load(rawBytes);
            var hssType = loadedAsm.GetType("HarmonyLib.HarmonySharedState");
            if (hssType != null)
            {
                var method = hssType.GetMethod("GetOrCreateSharedStateType", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (method != null)
                {
                    var res = method.Invoke(null, null);
                    Console.WriteLine($"Verification SUCCESS! GetOrCreateSharedStateType returned: {res}");
                }
                else
                {
                    Console.WriteLine("GetOrCreateSharedStateType method not found via reflection.");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Verification FAILED: {ex}");
            return 1;
        }

        return 0;
    }
}
