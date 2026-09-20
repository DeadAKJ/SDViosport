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
