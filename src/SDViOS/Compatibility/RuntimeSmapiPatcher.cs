using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using SDViOS.Diagnostics;
using SDViOS.Loader;

namespace SDViOS.Compatibility
{
    public static class RuntimeSmapiPatcher
    {
        private static DefaultAssemblyResolver CreateResolver(string targetDllPath)
        {
            var resolver = new DefaultAssemblyResolver();
            try
            {
                string? dir = Path.GetDirectoryName(targetDllPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    resolver.AddSearchDirectory(dir);

                if (!string.IsNullOrEmpty(GameHost.BundleDir) && Directory.Exists(GameHost.BundleDir))
                    resolver.AddSearchDirectory(GameHost.BundleDir);

                if (!string.IsNullOrEmpty(GameHost.GameRootDir) && Directory.Exists(GameHost.GameRootDir))
                    resolver.AddSearchDirectory(GameHost.GameRootDir);

                if (!string.IsNullOrEmpty(GameHost.DocumentsDir) && Directory.Exists(GameHost.DocumentsDir))
                    resolver.AddSearchDirectory(GameHost.DocumentsDir);

                string docSmapiInternal = Path.Combine(GameHost.DocumentsDir, "smapi-internal");
                if (Directory.Exists(docSmapiInternal))
                    resolver.AddSearchDirectory(docSmapiInternal);

                string bundleSmapiInternal = Path.Combine(GameHost.BundleDir, "smapi-internal");
                if (Directory.Exists(bundleSmapiInternal))
                    resolver.AddSearchDirectory(bundleSmapiInternal);
            }
            catch (Exception ex)
            {
                EngineLogger.LogWarning($"[RuntimeSmapiPatcher] Resolver directory setup warning: {ex.Message}");
            }
            return resolver;
        }

        public static string EnsureSmapiPatched(string smapiDllPath)
        {
            if (string.IsNullOrEmpty(smapiDllPath) || !File.Exists(smapiDllPath))
                return smapiDllPath;

            try
            {
                byte[] smapiBytes = File.ReadAllBytes(smapiDllPath);
                var resolver = CreateResolver(smapiDllPath);
                var readerParams = new ReaderParameters { AssemblyResolver = resolver };

                using var asm = AssemblyDefinition.ReadAssembly(new MemoryStream(smapiBytes), readerParams);
                var mod = asm.MainModule;
                bool modified = false;

                // 1. Neutralize SCore.Dispose (both parameterless and bool overloads)
                var scoreType = mod.GetType("StardewModdingAPI.Framework.SCore");
                if (scoreType != null)
                {
                    foreach (var m in scoreType.Methods.Where(m => m.Name == "Dispose").ToList())
                    {
                        if (m.HasBody && (m.Body.Instructions.Count > 1 || m.Body.Instructions[0].OpCode != OpCodes.Ret))
                        {
                            m.Body.Instructions.Clear();
                            m.Body.Variables.Clear();
                            m.Body.ExceptionHandlers.Clear();
                            m.Body.GetILProcessor().Emit(OpCodes.Ret);
                            modified = true;
                            EngineLogger.Log($"[RuntimeSmapiPatcher] Neutralized SCore.Dispose (params: {m.Parameters.Count}) to pure no-op.");
                        }
                    }

                    // 2. Neutralize SCore.OnGameExiting
                    var onExiting = scoreType.Methods.FirstOrDefault(m => m.Name == "OnGameExiting");
                    if (onExiting != null && onExiting.HasBody && (onExiting.Body.Instructions.Count > 1 || onExiting.Body.Instructions[0].OpCode != OpCodes.Ret))
                    {
                        onExiting.Body.Instructions.Clear();
                        onExiting.Body.Variables.Clear();
                        onExiting.Body.ExceptionHandlers.Clear();
                        onExiting.Body.GetILProcessor().Emit(OpCodes.Ret);
                        modified = true;
                        EngineLogger.Log("[RuntimeSmapiPatcher] Neutralized SCore.OnGameExiting to pure no-op.");
                    }
                }

                // 3. Neutralize SGameRunner.OnExiting
                var runnerType = mod.GetType("StardewModdingAPI.Framework.SGameRunner");
                if (runnerType != null)
                {
                    var onExiting = runnerType.Methods.FirstOrDefault(m => m.Name == "OnExiting");
                    if (onExiting != null && onExiting.HasBody && (onExiting.Body.Instructions.Count > 1 || onExiting.Body.Instructions[0].OpCode != OpCodes.Ret))
                    {
                        onExiting.Body.Instructions.Clear();
                        onExiting.Body.Variables.Clear();
                        onExiting.Body.ExceptionHandlers.Clear();
                        onExiting.Body.GetILProcessor().Emit(OpCodes.Ret);
                        modified = true;
                        EngineLogger.Log("[RuntimeSmapiPatcher] Neutralized SGameRunner.OnExiting to pure no-op.");
                    }
                }

                if (!modified)
                {
                    EngineLogger.Log($"[RuntimeSmapiPatcher] '{smapiDllPath}' is already patched.");
                    return smapiDllPath;
                }

                using var msOut = new MemoryStream();
                asm.Write(msOut);
                byte[] patchedBytes = msOut.ToArray();

                // Save patched assembly: try original path first, fall back to smapi-internal cache
                return SavePatchedAssembly(smapiDllPath, patchedBytes);
            }
            catch (Exception ex)
            {
                EngineLogger.LogError($"[RuntimeSmapiPatcher] Could not patch SMAPI assembly '{smapiDllPath}': {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                return smapiDllPath;
            }
        }

        public static string EnsureGameRunnerPatched(string sdvDllPath)
        {
            if (string.IsNullOrEmpty(sdvDllPath) || !File.Exists(sdvDllPath))
                return sdvDllPath;

            try
            {
                byte[] sdvBytes = File.ReadAllBytes(sdvDllPath);
                var resolver = CreateResolver(sdvDllPath);
                var readerParams = new ReaderParameters { AssemblyResolver = resolver };

                using var asm = AssemblyDefinition.ReadAssembly(new MemoryStream(sdvBytes), readerParams);
                var mod = asm.MainModule;
                bool modified = false;

                var runnerType = mod.GetType("StardewValley.GameRunner");
                if (runnerType != null)
                {
                    var killHandler = runnerType.Methods.FirstOrDefault(m => m.Name == "<.ctor>b__11_1");
                    if (killHandler != null && killHandler.HasBody && (killHandler.Body.Instructions.Count > 1 || killHandler.Body.Instructions[0].OpCode != OpCodes.Ret))
                    {
                        killHandler.Body.Instructions.Clear();
                        killHandler.Body.Variables.Clear();
                        killHandler.Body.ExceptionHandlers.Clear();
                        killHandler.Body.GetILProcessor().Emit(OpCodes.Ret);
                        modified = true;
                        EngineLogger.Log("[RuntimeSmapiPatcher] Neutralized GameRunner.<.ctor>b__11_1 (Process.Kill handler).");
                    }

                    // Scan all methods in GameRunner for any remaining Process.Kill instructions
                    foreach (var m in runnerType.Methods)
                    {
                        if (m.HasBody)
                        {
                            var il = m.Body.GetILProcessor();
                            foreach (var inst in m.Body.Instructions.ToList())
                            {
                                if (inst.Operand is MethodReference mr && mr.Name == "Kill" && mr.DeclaringType.Name == "Process")
                                {
                                    il.Replace(inst, il.Create(OpCodes.Nop));
                                    modified = true;
                                    EngineLogger.Log($"[RuntimeSmapiPatcher] Replaced Process.Kill with Nop in GameRunner.{m.Name}.");
                                }
                            }
                        }
                    }
                }

                var locMgrType = mod.GetType("StardewValley.LocalizedContentManager");
                if (locMgrType != null)
                {
                    var getContentRootMethod = locMgrType.Methods.FirstOrDefault(m => m.Name == "GetContentRoot");
                    if (getContentRootMethod != null && getContentRootMethod.HasBody)
                    {
                        foreach (var inst in getContentRootMethod.Body.Instructions)
                        {
                            // Replace ldc.i4.s 40 (Static | NonPublic) with 56 (Static | Public | NonPublic)
                            if (inst.OpCode == OpCodes.Ldc_I4_S && Convert.ToInt32(inst.Operand) == 40)
                            {
                                inst.Operand = (sbyte)56;
                                modified = true;
                                EngineLogger.Log("[RuntimeSmapiPatcher] Patched LocalizedContentManager.GetContentRoot BindingFlags 40 -> 56 (Static | Public | NonPublic).");
                            }
                        }
                    }
                }

                if (PatchNetRectangle(mod))
                    modified = true;

                if (!modified)
                {
                    EngineLogger.Log($"[RuntimeSmapiPatcher] '{sdvDllPath}' is already patched.");
                    return sdvDllPath;
                }

                using var msOut = new MemoryStream();
                asm.Write(msOut);
                byte[] patchedBytes = msOut.ToArray();

                return SavePatchedAssembly(sdvDllPath, patchedBytes);
            }
            catch (Exception ex)
            {
                EngineLogger.LogError($"[RuntimeSmapiPatcher] Could not patch SDV assembly '{sdvDllPath}': {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                return sdvDllPath;
            }
        }

        private static bool PatchNetRectangle(ModuleDefinition mod)
        {
            var netRectType = mod.GetType("Netcode.NetRectangle");
            if (netRectType == null)
            {
                EngineLogger.LogWarning("[RuntimeSmapiPatcher] Netcode.NetRectangle not found in assembly.");
                return false;
            }

            // Check if already patched and healthy
            var getValueExisting = netRectType.Methods.FirstOrDefault(m => m.Name == "get_Value" && m.ReturnType.FullName == "Microsoft.Xna.Framework.Rectangle");
            var getXExisting = netRectType.Methods.FirstOrDefault(m => m.Name == "get_X");

            bool isHealthy = getValueExisting != null &&
                             getValueExisting.HasBody &&
                             getValueExisting.Body.Instructions.Count >= 2 &&
                             getValueExisting.Body.Instructions[1].Operand is FieldReference gvFr &&
                             gvFr.FieldType.FullName == "T" &&
                             getXExisting != null &&
                             getXExisting.HasBody &&
                             getXExisting.Body.Instructions.Count >= 2 &&
                             getXExisting.Body.Instructions[1].OpCode == OpCodes.Ldflda &&
                             getXExisting.Body.Instructions[1].Operand is FieldReference gxFr &&
                             gxFr.FieldType.FullName == "T";

            if (isHealthy)
            {
                EngineLogger.Log("[RuntimeSmapiPatcher] Netcode.NetRectangle is already correctly patched and healthy. Skipping.");
                return false;
            }

            var setMethod = netRectType.Methods.FirstOrDefault(m => m.Name == "Set" && m.Parameters.Count == 1);
            if (setMethod == null)
            {
                EngineLogger.LogWarning("[RuntimeSmapiPatcher] Netcode.NetRectangle.Set(Rectangle) not found.");
                return false;
            }
            var rectType = setMethod.Parameters[0].ParameterType; // Concrete Microsoft.Xna.Framework.Rectangle

            var getTop = netRectType.Methods.FirstOrDefault(m => m.Name == "get_Top");
            if (getTop == null || !getTop.HasBody || getTop.Body.Instructions.Count < 2 || !(getTop.Body.Instructions[1].Operand is FieldReference))
            {
                EngineLogger.LogWarning("[RuntimeSmapiPatcher] Netcode.NetRectangle.get_Top instruction format unexpected.");
                return false;
            }
            var origValueFr = (FieldReference)getTop.Body.Instructions[1].Operand;

            // 1. Add or heal concrete public Rectangle get_Value()
            var getValue = getValueExisting;
            if (getValue == null)
            {
                getValue = new MethodDefinition(
                    "get_Value",
                    MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
                    rectType);
                netRectType.Methods.Add(getValue);
                EngineLogger.Log($"[RuntimeSmapiPatcher] Added NetRectangle.get_Value() with concrete return type: {rectType.FullName}.");
            }
            else
            {
                getValue.Body.Instructions.Clear();
                getValue.Body.Variables.Clear();
                getValue.Body.ExceptionHandlers.Clear();
                EngineLogger.Log("[RuntimeSmapiPatcher] Re-initializing existing NetRectangle.get_Value().");
            }
            var ilGet = getValue.Body.GetILProcessor();
            ilGet.Emit(OpCodes.Ldarg_0);
            ilGet.Emit(OpCodes.Ldfld, origValueFr);
            ilGet.Emit(OpCodes.Ret);

            // 2. Add or heal concrete public void set_Value(Rectangle value)
            var setValue = netRectType.Methods.FirstOrDefault(m => m.Name == "set_Value" && m.Parameters.Count == 1);
            if (setValue == null)
            {
                setValue = new MethodDefinition(
                    "set_Value",
                    MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
                    mod.TypeSystem.Void);
                setValue.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, rectType));
                var ilSet = setValue.Body.GetILProcessor();
                ilSet.Emit(OpCodes.Ldarg_0);
                ilSet.Emit(OpCodes.Ldarg_1);
                ilSet.Emit(OpCodes.Callvirt, setMethod);
                ilSet.Emit(OpCodes.Ret);
                netRectType.Methods.Add(setValue);
                EngineLogger.Log("[RuntimeSmapiPatcher] Added NetRectangle.set_Value(Rectangle).");
            }

            // Ensure 'Value' property exists and references concrete getValue / setValue without breaking Cecil metadata tokens
            var valProp = netRectType.Properties.FirstOrDefault(p => p.Name == "Value");
            if (valProp == null)
            {
                valProp = new PropertyDefinition("Value", PropertyAttributes.None, rectType)
                {
                    GetMethod = getValue,
                    SetMethod = setValue
                };
                netRectType.Properties.Add(valProp);
                EngineLogger.Log("[RuntimeSmapiPatcher] Added NetRectangle.Value property definition.");
            }
            else
            {
                valProp.PropertyType = rectType;
                valProp.GetMethod = getValue;
                valProp.SetMethod = setValue;
                EngineLogger.Log("[RuntimeSmapiPatcher] Re-linked existing NetRectangle.Value property to concrete get/set methods.");
            }

            // 3. Fix get_X, get_Y, get_Width, get_Height to load fields directly via ldflda origValueFr
            var writeDelta = netRectType.Methods.FirstOrDefault(m => m.Name == "WriteDelta");
            if (writeDelta != null && writeDelta.HasBody)
            {
                var rectFields = writeDelta.Body.Instructions
                    .Where(i => i.OpCode == OpCodes.Ldfld && i.Operand is FieldReference fr && fr.DeclaringType.Name == "Rectangle")
                    .Select(i => (FieldReference)i.Operand)
                    .ToList();
                var xField = rectFields.FirstOrDefault(f => f.Name == "X");
                var yField = rectFields.FirstOrDefault(f => f.Name == "Y");
                var wField = rectFields.FirstOrDefault(f => f.Name == "Width");
                var hField = rectFields.FirstOrDefault(f => f.Name == "Height");

                void FixGetter(string name, FieldReference? field)
                {
                    if (field == null) return;
                    var m = netRectType.Methods.FirstOrDefault(meth => meth.Name == name);
                    if (m == null || !m.HasBody) return;
                    m.Body.Instructions.Clear();
                    m.Body.Variables.Clear();
                    m.Body.ExceptionHandlers.Clear();
                    var il = m.Body.GetILProcessor();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldflda, origValueFr);
                    il.Emit(OpCodes.Ldfld, field);
                    il.Emit(OpCodes.Ret);
                    EngineLogger.Log($"[RuntimeSmapiPatcher] Patched NetRectangle.{name} to load field directly via origValueFr.");
                }

                FixGetter("get_X", xField);
                FixGetter("get_Y", yField);
                FixGetter("get_Width", wField);
                FixGetter("get_Height", hField);
            }

            // 4. Redirect all NetFieldBase<Rectangle, NetRectangle>::get_Value and set_Value call sites to concrete NetRectangle methods
            int replacedGet = 0;
            int replacedSet = 0;
            void PatchType(TypeDefinition t)
            {
                foreach (var m in t.Methods)
                {
                    if (m.HasBody && m.DeclaringType != netRectType)
                    {
                        for (int i = 0; i < m.Body.Instructions.Count; i++)
                        {
                            var inst = m.Body.Instructions[i];
                            if (inst.Operand is MethodReference mr)
                            {
                                if (mr.Name == "get_Value" &&
                                    mr.DeclaringType.FullName.Contains("NetFieldBase") &&
                                    mr.DeclaringType.FullName.Contains("Rectangle") &&
                                    mr.DeclaringType.FullName.Contains("NetRectangle"))
                                {
                                    inst.OpCode = OpCodes.Callvirt;
                                    inst.Operand = getValue;
                                    replacedGet++;
                                }
                                else if (mr.Name == "set_Value" &&
                                    mr.DeclaringType.FullName.Contains("NetFieldBase") &&
                                    mr.DeclaringType.FullName.Contains("Rectangle") &&
                                    mr.DeclaringType.FullName.Contains("NetRectangle"))
                                {
                                    inst.OpCode = OpCodes.Callvirt;
                                    inst.Operand = setValue;
                                    replacedSet++;
                                }
                            }
                        }
                    }
                }
                foreach (var nested in t.NestedTypes)
                    PatchType(nested);
            }

            foreach (var t in mod.Types)
                PatchType(t);

            EngineLogger.Log($"[RuntimeSmapiPatcher] Patched NetRectangle: added concrete Value getter/setter, fixed X/Y/W/H getters, redirected {replacedGet} get_Value and {replacedSet} set_Value calls.");
            return true;
        }

        private static string SavePatchedAssembly(string originalPath, byte[] patchedBytes)
        {
            // 1. Try writing in-place to originalPath
            try
            {
                string tmpFile = originalPath + ".patch_tmp";
                File.WriteAllBytes(tmpFile, patchedBytes);
                File.Move(tmpFile, originalPath, true);
                EngineLogger.Log($"[RuntimeSmapiPatcher] Successfully updated '{originalPath}' in-place ({patchedBytes.Length} bytes).");
                return originalPath;
            }
            catch (Exception ex)
            {
                EngineLogger.LogWarning($"[RuntimeSmapiPatcher] In-place write to '{originalPath}' failed ({ex.Message}). Caching to Documents/smapi-internal/...");
            }

            // 2. Fall back to Documents/smapi-internal/
            try
            {
                string internalDir = Path.Combine(GameHost.DocumentsDir, "smapi-internal");
                Directory.CreateDirectory(internalDir);
                string cachedPath = Path.Combine(internalDir, Path.GetFileName(originalPath));
                string tmpFile = cachedPath + ".patch_tmp";
                File.WriteAllBytes(tmpFile, patchedBytes);
                File.Move(tmpFile, cachedPath, true);
                EngineLogger.Log($"[RuntimeSmapiPatcher] Successfully saved patched copy to '{cachedPath}' ({patchedBytes.Length} bytes).");
                return cachedPath;
            }
            catch (Exception ex2)
            {
                EngineLogger.LogError($"[RuntimeSmapiPatcher] Failed to save patched assembly to cache: {ex2.Message}");
                return originalPath;
            }
        }
    }
}
