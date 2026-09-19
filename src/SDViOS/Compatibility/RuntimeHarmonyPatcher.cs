using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;
using SDViOS.Diagnostics;

namespace SDViOS.Compatibility
{
    public static class RuntimeHarmonyPatcher
    {
        public static void EnsureHarmonyPatched(string targetDll)
        {
            try
            {
                // 1. Prefer deploying verified embedded pre-patched 0Harmony.dll
                var currentAsm = Assembly.GetExecutingAssembly();
                string resourceName = "SDViOS.Resources.0Harmony.dll";
                using var resStream = currentAsm.GetManifestResourceStream(resourceName);
                if (resStream != null)
                {
                    string? dir = Path.GetDirectoryName(targetDll);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    using (var fs = new FileStream(targetDll, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        resStream.CopyTo(fs);
                    }
                    EngineLogger.Log($"[RuntimeHarmonyPatcher] Successfully deployed verified embedded 0Harmony.dll to '{targetDll}'.");
                    return;
                }
            }
            catch (Exception ex)
            {
                EngineLogger.LogWarning($"[RuntimeHarmonyPatcher] Could not deploy embedded 0Harmony.dll: {ex}");
            }

            if (!File.Exists(targetDll)) return;

            try
            {
                var readerParams = new ReaderParameters { ReadWrite = true };
                using var asm = AssemblyDefinition.ReadAssembly(targetDll, readerParams);
                var mod = asm.MainModule;

                bool modified = false;

                // 1. Inject HarmonySharedState type into 0Harmony.dll if not present
                var existingShared = mod.GetType("HarmonySharedState");
                if (existingShared == null)
                {
                    EngineLogger.Log($"[RuntimeHarmonyPatcher] Injecting HarmonySharedState into '{targetDll}'...");
                    var sharedType = new TypeDefinition(
                        "",
                        "HarmonySharedState",
                        Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Abstract | Mono.Cecil.TypeAttributes.Sealed,
                        mod.TypeSystem.Object
                    );
                    mod.Types.Add(sharedType);

                    var versionField = new FieldDefinition(
                        "version",
                        Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static,
                        mod.TypeSystem.Int32
                    );
                    sharedType.Fields.Add(versionField);

                    var stateTypeRef = mod.ImportReference(typeof(Dictionary<MethodBase, byte[]>));
                    var stateField = new FieldDefinition(
                        "state",
                        Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static,
                        stateTypeRef
                    );
                    sharedType.Fields.Add(stateField);

                    var origTypeRef = mod.ImportReference(typeof(Dictionary<MethodInfo, MethodBase>));
                    var origField = new FieldDefinition(
                        "originals",
                        Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static,
                        origTypeRef
                    );
                    sharedType.Fields.Add(origField);

                    modified = true;
                }

                var t = mod.GetType("HarmonyLib.HarmonySharedState");
                if (t != null)
                {
                    // 2. Neutralize DetourHelper.Runtime.add_OnMethodCompiled in cctor
                    var cctor = t.Methods.FirstOrDefault(m => m.Name == ".cctor");
                    if (cctor != null && cctor.HasBody)
                    {
                        for (int i = 0; i < cctor.Body.Instructions.Count; i++)
                        {
                            var ins = cctor.Body.Instructions[i];
                            if (ins.OpCode == OpCodes.Call && ins.Operand?.ToString()?.Contains("DetourHelper::get_Runtime") == true)
                            {
                                EngineLogger.Log("[RuntimeHarmonyPatcher] Neutralizing DetourHelper call in .cctor with ret...");
                                ins.OpCode = OpCodes.Ret;
                                ins.Operand = null;
                                while (cctor.Body.Instructions.Count > i + 1)
                                {
                                    cctor.Body.Instructions.RemoveAt(i + 1);
                                }
                                cctor.Body.ExceptionHandlers.Clear();
                                modified = true;
                                break;
                            }
                        }

                        if (cctor.Body.ExceptionHandlers.Count > 0 && cctor.Body.Instructions.Any(ins => ins.OpCode == OpCodes.Ret))
                        {
                            cctor.Body.ExceptionHandlers.Clear();
                            modified = true;
                        }
                    }

                    // 3. Patch GetOrCreateSharedStateType to directly return typeof(HarmonySharedState)
                    var getOrCreate = t.Methods.FirstOrDefault(m => m.Name == "GetOrCreateSharedStateType");
                    var targetSharedType = mod.GetType("HarmonySharedState");
                    if (getOrCreate != null && targetSharedType != null)
                    {
                        // Check if already returning typeof(HarmonySharedState)
                        bool alreadyPatched = getOrCreate.Body.Instructions.Count <= 3 &&
                                              getOrCreate.Body.Instructions.Any(ins => ins.OpCode == OpCodes.Ldtoken);
                        if (!alreadyPatched)
                        {
                            EngineLogger.Log("[RuntimeHarmonyPatcher] Patching GetOrCreateSharedStateType to directly return HarmonySharedState...");
                            getOrCreate.Body.Instructions.Clear();
                            getOrCreate.Body.Variables.Clear();
                            getOrCreate.Body.ExceptionHandlers.Clear();
                            var il = getOrCreate.Body.GetILProcessor();
                            il.Emit(OpCodes.Ldtoken, targetSharedType);
                            var getTypeFromHandle = mod.ImportReference(typeof(Type).GetMethod("GetTypeFromHandle", new[] { typeof(RuntimeTypeHandle) }));
                            il.Emit(OpCodes.Call, getTypeFromHandle);
                            il.Emit(OpCodes.Ret);
                            modified = true;
                        }
                        else
                        {
                            // Clean up dangling ExceptionHandlers or Variables if left by previous patch
                            if (getOrCreate.Body.ExceptionHandlers.Count > 0)
                            {
                                EngineLogger.Log("[RuntimeHarmonyPatcher] Cleaning dangling ExceptionHandlers from GetOrCreateSharedStateType...");
                                getOrCreate.Body.ExceptionHandlers.Clear();
                                modified = true;
                            }
                            if (getOrCreate.Body.Variables.Count > 0)
                            {
                                getOrCreate.Body.Variables.Clear();
                                modified = true;
                            }
                        }
                    }
                }

                if (modified)
                {
                    asm.Write();
                    EngineLogger.Log($"[RuntimeHarmonyPatcher] Successfully patched '{targetDll}' for iOS W^X.");
                }
            }
            catch (Exception ex)
            {
                EngineLogger.LogWarning($"[RuntimeHarmonyPatcher] Warning while checking '{targetDll}': {ex}");
            }
        }
    }
}
