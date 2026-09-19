using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using SDViOS.Diagnostics;

namespace SDViOS.Compatibility
{
    public static class RuntimeSmapiPatcher
    {
        public static void EnsureSmapiPatched(string smapiDllPath)
        {
            if (string.IsNullOrEmpty(smapiDllPath) || !File.Exists(smapiDllPath))
                return;

            try
            {
                var readerParams = new ReaderParameters { ReadWrite = true };
                using var asm = AssemblyDefinition.ReadAssembly(smapiDllPath, readerParams);
                var mod = asm.MainModule;
                bool modified = false;

                // 1. Neutralize SCore.OnGameExiting
                var scoreType = mod.GetType("StardewModdingAPI.Framework.SCore");
                if (scoreType != null)
                {
                    var onExiting = scoreType.Methods.FirstOrDefault(m => m.Name == "OnGameExiting");
                    if (onExiting != null && onExiting.HasBody && onExiting.Body.Instructions.Count > 1)
                    {
                        onExiting.Body.Instructions.Clear();
                        onExiting.Body.Variables.Clear();
                        onExiting.Body.ExceptionHandlers.Clear();
                        var il = onExiting.Body.GetILProcessor();
                        il.Emit(OpCodes.Ret);
                        modified = true;
                        EngineLogger.Log("[RuntimeSmapiPatcher] Neutralized SCore.OnGameExiting (prevents startup disposal).");
                    }
                }

                // 2. Neutralize SGameRunner.OnExiting
                var runnerType = mod.GetType("StardewModdingAPI.Framework.SGameRunner");
                if (runnerType != null)
                {
                    var onExiting = runnerType.Methods.FirstOrDefault(m => m.Name == "OnExiting");
                    if (onExiting != null && onExiting.HasBody && onExiting.Body.Instructions.Count > 1)
                    {
                        onExiting.Body.Instructions.Clear();
                        onExiting.Body.Variables.Clear();
                        onExiting.Body.ExceptionHandlers.Clear();
                        var il = onExiting.Body.GetILProcessor();
                        il.Emit(OpCodes.Ret);
                        modified = true;
                        EngineLogger.Log("[RuntimeSmapiPatcher] Neutralized SGameRunner.OnExiting (prevents disposal invocation).");
                    }
                }

                if (modified)
                {
                    asm.Write();
                    EngineLogger.Log($"[RuntimeSmapiPatcher] Successfully saved patched StardewModdingAPI.dll at '{smapiDllPath}'.");
                }
            }
            catch (Exception ex)
            {
                EngineLogger.LogWarning($"[RuntimeSmapiPatcher] Could not patch '{smapiDllPath}': {ex.Message}");
            }
        }

        public static void EnsureGameRunnerPatched(string sdvDllPath)
        {
            if (string.IsNullOrEmpty(sdvDllPath) || !File.Exists(sdvDllPath))
                return;

            try
            {
                var readerParams = new ReaderParameters { ReadWrite = true };
                using var asm = AssemblyDefinition.ReadAssembly(sdvDllPath, readerParams);
                var mod = asm.MainModule;
                bool modified = false;

                var runnerType = mod.GetType("StardewValley.GameRunner");
                if (runnerType != null)
                {
                    foreach (var m in runnerType.Methods)
                    {
                        if (m.HasBody && m.Body.Instructions.Any(i => i.Operand is MethodReference mr && mr.Name == "Kill" && mr.DeclaringType.Name == "Process"))
                        {
                            m.Body.Instructions.Clear();
                            m.Body.Variables.Clear();
                            m.Body.ExceptionHandlers.Clear();
                            var il = m.Body.GetILProcessor();
                            il.Emit(OpCodes.Ret);
                            modified = true;
                            EngineLogger.Log($"[RuntimeSmapiPatcher] Neutralized Process.Kill in GameRunner.{m.Name}.");
                        }
                    }
                }

                if (modified)
                {
                    asm.Write();
                    EngineLogger.Log($"[RuntimeSmapiPatcher] Successfully saved patched Stardew Valley.dll at '{sdvDllPath}'.");
                }
            }
            catch (Exception ex)
            {
                EngineLogger.LogWarning($"[RuntimeSmapiPatcher] Could not patch '{sdvDllPath}': {ex.Message}");
            }
        }
    }
}
