using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

class Program
{
    static int Main(string[] args)
    {
        string dllPath = args.Length > 0 ? args[0] : string.Empty;
        if (string.IsNullOrEmpty(dllPath))
        {
            string candidate1 = Path.Combine(Directory.GetCurrentDirectory(), "lib", "MonoGame.Framework.dll");
            string candidate2 = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "lib", "MonoGame.Framework.dll"));
            string candidate3 = @"c:\Users\User\Documents\GitHub\SDVios\lib\MonoGame.Framework.dll";
            if (File.Exists(candidate1)) dllPath = candidate1;
            else if (File.Exists(candidate2)) dllPath = candidate2;
            else dllPath = candidate3;
        }
        if (!File.Exists(dllPath))
        {
            Console.WriteLine($"Error: {dllPath} not found.");
            return 1;
        }

        if (args.Contains("--inspect-sei"))
        {
            var asm = AssemblyDefinition.ReadAssembly(dllPath);
            var wb = asm.MainModule.GetType("Microsoft.Xna.Framework.Audio.WaveBank");
            var m = wb.Methods.First(x => x.Name == "GetSoundEffectInstance");
            Console.WriteLine($"Method: {m.Name}");
            Console.WriteLine($"Variables count: {m.Body.Variables.Count}");
            foreach (var v in m.Body.Variables)
                Console.WriteLine($"  Var {v.Index}: {v.VariableType.FullName}");
            Console.WriteLine($"Exception Handlers: {m.Body.ExceptionHandlers.Count}");
            foreach (var eh in m.Body.ExceptionHandlers)
                Console.WriteLine($"  EH: Type={eh.HandlerType}, Try: {eh.TryStart?.Offset:X4}-{eh.TryEnd?.Offset:X4}, Handler: {eh.HandlerStart?.Offset:X4}-{eh.HandlerEnd?.Offset:X4}");
            foreach (var inst in m.Body.Instructions)
                Console.WriteLine($"  {inst.Offset:X4}: {inst.OpCode} {inst.Operand}");

            var allLoads = wb.Methods.Where(x => x.Name == "LoadSoundEffect").ToList();
            Console.WriteLine($"Found {allLoads.Count} LoadSoundEffect methods");
            for (int li = 0; li < allLoads.Count; li++)
            {
                var loadM = allLoads[li];
                Console.WriteLine($"\n--- LoadSoundEffect #{li} ---");
                Console.WriteLine($"Method: {loadM.Name}, ReturnType: {loadM.ReturnType}");
                Console.WriteLine($"Variables count: {loadM.Body.Variables.Count}");
                foreach (var v in loadM.Body.Variables)
                    Console.WriteLine($"  Var {v.Index}: {v.VariableType.FullName}");
                Console.WriteLine($"Exception Handlers: {loadM.Body.ExceptionHandlers.Count}");
                foreach (var eh in loadM.Body.ExceptionHandlers)
                    Console.WriteLine($"  EH: Type={eh.HandlerType}, Try: {eh.TryStart?.Offset:X4}-{eh.TryEnd?.Offset:X4}, Handler: {eh.HandlerStart?.Offset:X4}-{eh.HandlerEnd?.Offset:X4}");
                foreach (var inst in loadM.Body.Instructions)
                    Console.WriteLine($"  {inst.Offset:X4}: {inst.OpCode} {inst.Operand}");
            }
            return 0;
        }

        if (args.Contains("--verify"))
        {
            var asm = AssemblyDefinition.ReadAssembly(dllPath);
            var xs = asm.MainModule.GetType("Microsoft.Xna.Framework.Audio.XactSound");
            var ctor = xs.Methods.First(x => x.IsConstructor && x.Parameters.Count == 3 && x.Parameters[0].ParameterType.Name == "AudioEngine");
            Console.WriteLine("Last 10 instructions of XactSound..ctor:");
            var list = ctor.Body.Instructions.ToList();
            for (int i = Math.Max(0, list.Count - 10); i < list.Count; i++)
                Console.WriteLine($"  {list[i].Offset:X4}: {list[i].OpCode} {list[i].Operand}");

            Console.WriteLine("\nBranches into the end of XactSound..ctor:");
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Operand is Instruction target && target.Offset >= list[list.Count - 5].Offset)
                    Console.WriteLine($"  {list[i].Offset:X4}: {list[i].OpCode} -> {target.Offset:X4} ({target.OpCode})");
            }

            Console.WriteLine("\nAudioCategory methods:");
            var cat = asm.MainModule.GetType("Microsoft.Xna.Framework.Audio.AudioCategory");
            foreach (var m in cat.Methods.Where(m => !m.IsConstructor))
            {
                Console.WriteLine($"AudioCategory.{m.Name}: {m.Body.Instructions.Count} instructions");
                foreach (var inst in m.Body.Instructions)
                    Console.WriteLine($"    {inst.Offset:X4}: {inst.OpCode} {inst.Operand}");
            }
            return 0;
        }

        Console.WriteLine($"Patching MonoGame at: {dllPath}");
        var assembly = AssemblyDefinition.ReadAssembly(dllPath, new ReaderParameters { ReadWrite = true });
        var module = assembly.MainModule;

        var titleContainerType = module.GetType("Microsoft.Xna.Framework.TitleContainer");
        if (titleContainerType != null)
        {
            // 0a. Make get_Location and set_Location public
            var getLocMethod = titleContainerType.Methods.FirstOrDefault(m => m.Name == "get_Location");
            if (getLocMethod != null)
            {
                getLocMethod.Attributes = (getLocMethod.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Public;
                Console.WriteLine("Made TitleContainer.get_Location public.");
            }
            var setLocMethod = titleContainerType.Methods.FirstOrDefault(m => m.Name == "set_Location");
            if (setLocMethod != null)
            {
                setLocMethod.Attributes = (setLocMethod.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Public;
                Console.WriteLine("Made TitleContainer.set_Location public.");
            }

            // 0b. Patch PlatformOpenStream to search Location, Documents, Documents/StardewValley, and App Bundle
            var platOpenMethod = titleContainerType.Methods.FirstOrDefault(m => m.Name == "PlatformOpenStream");
            var platInitMethod = titleContainerType.Methods.FirstOrDefault(m => m.Name == "PlatformInit");

            if (platOpenMethod != null && platInitMethod != null && getLocMethod != null)
            {
                // Grab NSBundle methods from PlatformInit instructions
                var getMainBundle = platInitMethod.Body.Instructions.First(i => i.OpCode == OpCodes.Call && ((MethodReference)i.Operand).Name == "get_MainBundle").Operand as MethodReference;
                var getResourcePath = platInitMethod.Body.Instructions.First(i => i.OpCode == OpCodes.Callvirt && ((MethodReference)i.Operand).Name == "get_ResourcePath").Operand as MethodReference;

                var pathCombine2 = module.ImportReference(typeof(Path).GetMethod("Combine", new[] { typeof(string), typeof(string) }));
                var pathCombine3 = module.ImportReference(typeof(Path).GetMethod("Combine", new[] { typeof(string), typeof(string), typeof(string) }));
                var fileExists = module.ImportReference(typeof(File).GetMethod("Exists", new[] { typeof(string) }));
                var fileOpenRead = module.ImportReference(typeof(File).GetMethod("OpenRead", new[] { typeof(string) }));
                var stringIsNullOrEmpty = module.ImportReference(typeof(string).GetMethod("IsNullOrEmpty", new[] { typeof(string) }));
                var getFolderPath = module.ImportReference(typeof(Environment).GetMethod("GetFolderPath", new[] { typeof(Environment.SpecialFolder) }));

                platOpenMethod.Body.Instructions.Clear();
                platOpenMethod.Body.Variables.Clear();

                // V_0: locPath (string)
                // V_1: docs (string)
                // V_2: docPath (string)
                // V_3: bundle (string)
                // V_4: bundlePath (string)
                platOpenMethod.Body.Variables.Add(new VariableDefinition(module.TypeSystem.String));
                platOpenMethod.Body.Variables.Add(new VariableDefinition(module.TypeSystem.String));
                platOpenMethod.Body.Variables.Add(new VariableDefinition(module.TypeSystem.String));
                platOpenMethod.Body.Variables.Add(new VariableDefinition(module.TypeSystem.String));
                platOpenMethod.Body.Variables.Add(new VariableDefinition(module.TypeSystem.String));

                var il = platOpenMethod.Body.GetILProcessor();

                var lblCheckDocs = il.Create(OpCodes.Ldc_I4_S, (sbyte)5); // Environment.SpecialFolder.MyDocuments
                var lblCheckBundle = il.Create(OpCodes.Call, getMainBundle);
                var lblCheckDocsSubdir = il.Create(OpCodes.Ldloc_1);
                var lblFallback = il.Create(OpCodes.Ldloc_0);

                // 1. locPath = Path.Combine(TitleContainer.Location, safeName);
                il.Emit(OpCodes.Call, getLocMethod);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, pathCombine2);
                il.Emit(OpCodes.Stloc_0);

                // if (File.Exists(locPath)) return File.OpenRead(locPath);
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Call, fileExists);
                il.Emit(OpCodes.Brfalse_S, lblCheckDocs);
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Call, fileOpenRead);
                il.Emit(OpCodes.Ret);

                // 2. CHECK_DOCS: docs = Environment.GetFolderPath(SpecialFolder.MyDocuments);
                il.Append(lblCheckDocs);
                il.Emit(OpCodes.Call, getFolderPath);
                il.Emit(OpCodes.Stloc_1);

                // if (!string.IsNullOrEmpty(docs))
                il.Emit(OpCodes.Ldloc_1);
                il.Emit(OpCodes.Call, stringIsNullOrEmpty);
                il.Emit(OpCodes.Brtrue_S, lblCheckBundle);

                // docPath = Path.Combine(docs, safeName);
                il.Emit(OpCodes.Ldloc_1);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, pathCombine2);
                il.Emit(OpCodes.Stloc_2);

                // if (File.Exists(docPath)) return File.OpenRead(docPath);
                il.Emit(OpCodes.Ldloc_2);
                il.Emit(OpCodes.Call, fileExists);
                il.Emit(OpCodes.Brfalse_S, lblCheckDocsSubdir);
                il.Emit(OpCodes.Ldloc_2);
                il.Emit(OpCodes.Call, fileOpenRead);
                il.Emit(OpCodes.Ret);

                // CHECK_DOCS_SUBDIR: docPath = Path.Combine(docs, "StardewValley", safeName);
                il.Append(lblCheckDocsSubdir);
                il.Emit(OpCodes.Ldstr, "StardewValley");
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, pathCombine3);
                il.Emit(OpCodes.Stloc_2);

                // if (File.Exists(docPath)) return File.OpenRead(docPath);
                il.Emit(OpCodes.Ldloc_2);
                il.Emit(OpCodes.Call, fileExists);
                il.Emit(OpCodes.Brfalse_S, lblCheckBundle);
                il.Emit(OpCodes.Ldloc_2);
                il.Emit(OpCodes.Call, fileOpenRead);
                il.Emit(OpCodes.Ret);

                // 3. CHECK_BUNDLE: bundle = NSBundle.MainBundle.ResourcePath;
                il.Append(lblCheckBundle);
                il.Emit(OpCodes.Callvirt, getResourcePath);
                il.Emit(OpCodes.Stloc_3);

                // if (!string.IsNullOrEmpty(bundle))
                il.Emit(OpCodes.Ldloc_3);
                il.Emit(OpCodes.Call, stringIsNullOrEmpty);
                il.Emit(OpCodes.Brtrue_S, lblFallback);

                // bundlePath = Path.Combine(bundle, safeName);
                il.Emit(OpCodes.Ldloc_3);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, pathCombine2);
                il.Emit(OpCodes.Stloc_S, platOpenMethod.Body.Variables[4]);

                // if (File.Exists(bundlePath)) return File.OpenRead(bundlePath);
                il.Emit(OpCodes.Ldloc_S, platOpenMethod.Body.Variables[4]);
                il.Emit(OpCodes.Call, fileExists);
                il.Emit(OpCodes.Brfalse_S, lblFallback);
                il.Emit(OpCodes.Ldloc_S, platOpenMethod.Body.Variables[4]);
                il.Emit(OpCodes.Call, fileOpenRead);
                il.Emit(OpCodes.Ret);

                // 4. FALLBACK: return File.OpenRead(locPath);
                il.Append(lblFallback);
                il.Emit(OpCodes.Call, fileOpenRead);
                il.Emit(OpCodes.Ret);

                Console.WriteLine("Patched TitleContainer.PlatformOpenStream with multi-path resolution.");
                foreach (var inst in platOpenMethod.Body.Instructions)
                {
                    Console.WriteLine($"  {inst.Offset:X4}: {inst.OpCode} {inst.Operand} (Type: {inst.Operand?.GetType().Name})");
                }
            }
        }

        // 1. Remove duplicate empty OggStreamSoundEffect
        var oggTypes = module.Types.Where(t => t.Name == "OggStreamSoundEffect").ToList();
        if (oggTypes.Count > 1)
        {
            var emptyOne = oggTypes.FirstOrDefault(t => t.Methods.Count == 0);
            if (emptyOne != null)
            {
                module.Types.Remove(emptyOne);
                Console.WriteLine("Removed empty duplicate OggStreamSoundEffect type.");
            }
        }

        // 2. ReverbSettings: add parameterless constructor
        var reverbSettingsType = module.GetType("Microsoft.Xna.Framework.Audio.ReverbSettings");
        var dspParameterType = module.GetType("Microsoft.Xna.Framework.Audio.DspParameter");
        var audioEngineType = module.GetType("Microsoft.Xna.Framework.Audio.AudioEngine");

        if (reverbSettingsType == null || audioEngineType == null || dspParameterType == null)
        {
            Console.WriteLine("Error: Required audio types not found in MonoGame.");
            return 1;
        }

        var paramsField = reverbSettingsType.Fields.FirstOrDefault(f => f.Name == "_parameters");
        if (paramsField == null)
        {
            Console.WriteLine("Error: _parameters field not found in ReverbSettings.");
            return 1;
        }

        // Check or create parameterless ctor for ReverbSettings
        var defaultCtor = reverbSettingsType.Methods.FirstOrDefault(m => m.IsConstructor && !m.HasParameters && !m.IsStatic);
        if (defaultCtor == null)
        {
            var objCtor = module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes));
            defaultCtor = new MethodDefinition(".ctor",
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                module.TypeSystem.Void);

            var il = defaultCtor.Body.GetILProcessor();
            // base..ctor()
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, objCtor);

            // this._parameters = new DspParameter[22];
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4_S, (sbyte)22);
            il.Emit(OpCodes.Newarr, dspParameterType);
            il.Emit(OpCodes.Stfld, paramsField);
            il.Emit(OpCodes.Ret);

            reverbSettingsType.Methods.Add(defaultCtor);
            Console.WriteLine("Created parameterless constructor for ReverbSettings.");
        }

        // 3. Patch AudioEngine.GetReverbSettings()
        var getReverbMethod = audioEngineType.Methods.FirstOrDefault(m => m.Name == "GetReverbSettings");
        var reverbField = audioEngineType.Fields.FirstOrDefault(f => f.Name == "_reverbSettings");

        if (getReverbMethod != null && reverbField != null)
        {
            getReverbMethod.Body.Instructions.Clear();
            var il = getReverbMethod.Body.GetILProcessor();

            // if (this._reverbSettings == null) this._reverbSettings = new ReverbSettings();
            // return this._reverbSettings;
            var lblReturn = il.Create(OpCodes.Ldarg_0);

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, reverbField);
            il.Emit(OpCodes.Brtrue_S, lblReturn);

            // Create new ReverbSettings
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Newobj, defaultCtor);
            il.Emit(OpCodes.Stfld, reverbField);

            il.Append(lblReturn);
            il.Emit(OpCodes.Ldfld, reverbField);
            il.Emit(OpCodes.Ret);

            Console.WriteLine("Successfully patched AudioEngine.GetReverbSettings() to return non-null ReverbSettings.");
        }

        // 4. Also safeguard ReverbSettings.set_Item
        var setItemMethod = reverbSettingsType.Methods.FirstOrDefault(m => m.Name == "set_Item");
        if (setItemMethod != null)
        {
            // Ensure this._parameters is allocated
            var firstInst = setItemMethod.Body.Instructions.FirstOrDefault();
            if (firstInst != null)
            {
                var il = setItemMethod.Body.GetILProcessor();
                var lblExisting = firstInst;

                var checkNull = il.Create(OpCodes.Ldarg_0);
                il.InsertBefore(lblExisting, checkNull);
                il.InsertBefore(lblExisting, il.Create(OpCodes.Ldfld, paramsField));
                il.InsertBefore(lblExisting, il.Create(OpCodes.Brtrue_S, lblExisting));

                il.InsertBefore(lblExisting, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(lblExisting, il.Create(OpCodes.Ldc_I4_S, (sbyte)22));
                il.InsertBefore(lblExisting, il.Create(OpCodes.Newarr, dspParameterType));
                il.InsertBefore(lblExisting, il.Create(OpCodes.Stfld, paramsField));

                Console.WriteLine("Safeguarded ReverbSettings.set_Item against null _parameters.");
            }
        }

        // 5. WaveBank: Lazy SoundEffect loading
        var waveBankType = module.GetType("Microsoft.Xna.Framework.Audio.WaveBank");
        var soundEffectType = module.GetType("Microsoft.Xna.Framework.Audio.SoundEffect");
        if (waveBankType != null && soundEffectType != null)
        {
            var streamInfoType = waveBankType.NestedTypes.FirstOrDefault(t => t.Name == "StreamInfo");
            var soundsField = waveBankType.Fields.FirstOrDefault(f => f.Name == "_sounds");
            var streamsField = waveBankType.Fields.FirstOrDefault(f => f.Name == "_streams");
            var streamingField = waveBankType.Fields.FirstOrDefault(f => f.Name == "_streaming");
            var fileNameField = waveBankType.Fields.FirstOrDefault(f => f.Name == "_waveBankFileName");
            var playRegionField = waveBankType.Fields.FirstOrDefault(f => f.Name == "_playRegionOffset");

            var decodeFormatMethod = waveBankType.Methods.FirstOrDefault(m => m.Name == "DecodeFormat");
            var miniFormatTagType = module.GetType("Microsoft.Xna.Framework.Audio.MiniFormatTag");

            var seCtor7 = soundEffectType.Methods.FirstOrDefault(m => m.IsConstructor && m.Parameters.Count == 7 && m.Parameters[1].ParameterType.FullName == "System.Byte[]");
            var getPooledInstanceMethod = soundEffectType.Methods.FirstOrDefault(m => m.Name == "GetPooledInstance");

            var openStreamMethod = audioEngineType.Methods.FirstOrDefault(m => m.Name == "OpenStream");

            var streamSeekMethod = module.ImportReference(typeof(Stream).GetMethod("Seek", new[] { typeof(long), typeof(SeekOrigin) }));
            var streamDisposeMethod = module.ImportReference(typeof(IDisposable).GetMethod("Dispose"));

            if (streamInfoType != null && soundsField != null && streamsField != null &&
                fileNameField != null && playRegionField != null && decodeFormatMethod != null &&
                miniFormatTagType != null && seCtor7 != null && getPooledInstanceMethod != null &&
                openStreamMethod != null)
            {
                var fileOffsetField = streamInfoType.Fields.FirstOrDefault(f => f.Name == "FileOffset");
                var fileLengthField = streamInfoType.Fields.FirstOrDefault(f => f.Name == "FileLength");
                var formatField = streamInfoType.Fields.FirstOrDefault(f => f.Name == "Format");
                var loopStartField = streamInfoType.Fields.FirstOrDefault(f => f.Name == "LoopStart");
                var loopLengthField = streamInfoType.Fields.FirstOrDefault(f => f.Name == "LoopLength");

                // 5a. Bypass synchronous decoding loop in 5-param constructor
                var ctor5 = waveBankType.Methods.FirstOrDefault(m => m.IsConstructor && m.Parameters.Count == 5);
                if (ctor5 != null)
                {
                    // Look for `ldarg.0` followed by `ldfld _streaming` (or `nop`) followed by branch past synchronous loop
                    for (int i = 0; i < ctor5.Body.Instructions.Count - 2; i++)
                    {
                        var prevInst = ctor5.Body.Instructions[i];
                        var inst = ctor5.Body.Instructions[i + 1];
                        var nextInst = ctor5.Body.Instructions[i + 2];
                        if (prevInst.OpCode == OpCodes.Ldarg_0 &&
                            (inst.OpCode == OpCodes.Ldfld && inst.Operand == streamingField || inst.OpCode == OpCodes.Nop) &&
                            (nextInst.OpCode == OpCodes.Brtrue || nextInst.OpCode == OpCodes.Brtrue_S || nextInst.OpCode == OpCodes.Br))
                        {
                            var target = nextInst.Operand as Instruction;
                            if (target != null && target.Offset >= 0x0590)
                            {
                                // Nop out ldarg.0 and ldfld so the evaluation stack remains depth 0, then br to target!
                                prevInst.OpCode = OpCodes.Nop;
                                prevInst.Operand = null;
                                inst.OpCode = OpCodes.Nop;
                                inst.Operand = null;
                                nextInst.OpCode = OpCodes.Br;
                                nextInst.Operand = target;
                                Console.WriteLine($"Patched WaveBank 5-param ctor to bypass synchronous decoding loop cleanly (balanced stack -> {target.Offset:X4}).");
                                break;
                            }
                        }
                    }
                }

                // 5b. Add private SoundEffect LoadSoundEffect(int trackIndex) to WaveBank
                var existingLoads = waveBankType.Methods.Where(m => m.Name == "LoadSoundEffect").ToList();
                foreach (var ex in existingLoads)
                {
                    waveBankType.Methods.Remove(ex);
                }
                var loadSoundMethod = new MethodDefinition("LoadSoundEffect",
                    MethodAttributes.Private | MethodAttributes.HideBySig,
                    soundEffectType);
                loadSoundMethod.Parameters.Add(new ParameterDefinition("trackIndex", ParameterAttributes.None, module.TypeSystem.Int32));

                // Locals:
                // V_0: StreamInfo stream
                // V_1: Stream s
                // V_2: BinaryReader br
                // V_3: byte[] data
                // V_4: MiniFormatTag codec
                // V_5: int channels
                // V_6: int rate
                // V_7: int alignment
                // V_8: SoundEffect se
                loadSoundMethod.Body.Variables.Add(new VariableDefinition(streamInfoType));
                loadSoundMethod.Body.Variables.Add(new VariableDefinition(module.ImportReference(typeof(Stream))));
                loadSoundMethod.Body.Variables.Add(new VariableDefinition(module.ImportReference(typeof(BinaryReader))));
                loadSoundMethod.Body.Variables.Add(new VariableDefinition(module.ImportReference(typeof(byte[]))));
                loadSoundMethod.Body.Variables.Add(new VariableDefinition(miniFormatTagType));
                loadSoundMethod.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Int32));
                loadSoundMethod.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Int32));
                loadSoundMethod.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Int32));
                loadSoundMethod.Body.Variables.Add(new VariableDefinition(soundEffectType));

                var brCtor = module.ImportReference(typeof(BinaryReader).GetConstructor(new[] { typeof(Stream) }));
                var brReadBytes = module.ImportReference(typeof(BinaryReader).GetMethod("ReadBytes", new[] { typeof(int) }));

                var ilLd = loadSoundMethod.Body.GetILProcessor();

                // if (this._sounds == null) return null;
                var lblCheckTrack = ilLd.Create(OpCodes.Ldarg_1);
                ilLd.Emit(OpCodes.Ldarg_0);
                ilLd.Emit(OpCodes.Ldfld, soundsField);
                ilLd.Emit(OpCodes.Brtrue_S, lblCheckTrack);
                ilLd.Emit(OpCodes.Ldnull);
                ilLd.Emit(OpCodes.Ret);

                // if (trackIndex < 0 || trackIndex >= this._sounds.Length) return null;
                ilLd.Append(lblCheckTrack);
                ilLd.Emit(OpCodes.Ldc_I4_0);
                var lblCheckUpperBound = ilLd.Create(OpCodes.Ldarg_1);
                ilLd.Emit(OpCodes.Bge_S, lblCheckUpperBound);
                ilLd.Emit(OpCodes.Ldnull);
                ilLd.Emit(OpCodes.Ret);

                ilLd.Append(lblCheckUpperBound);
                ilLd.Emit(OpCodes.Ldarg_0);
                ilLd.Emit(OpCodes.Ldfld, soundsField);
                ilLd.Emit(OpCodes.Ldlen);
                ilLd.Emit(OpCodes.Conv_I4);
                var lblCheckCached = ilLd.Create(OpCodes.Ldarg_0);
                ilLd.Emit(OpCodes.Blt_S, lblCheckCached);
                ilLd.Emit(OpCodes.Ldnull);
                ilLd.Emit(OpCodes.Ret);

                // if (this._sounds[trackIndex] != null) return this._sounds[trackIndex];
                ilLd.Append(lblCheckCached);
                ilLd.Emit(OpCodes.Ldfld, soundsField);
                ilLd.Emit(OpCodes.Ldarg_1);
                ilLd.Emit(OpCodes.Ldelem_Ref);
                ilLd.Emit(OpCodes.Stloc_S, loadSoundMethod.Body.Variables[8]);
                ilLd.Emit(OpCodes.Ldloc_S, loadSoundMethod.Body.Variables[8]);
                var lblLoadTrack = ilLd.Create(OpCodes.Ldarg_0);
                ilLd.Emit(OpCodes.Brfalse_S, lblLoadTrack);
                ilLd.Emit(OpCodes.Ldloc_S, loadSoundMethod.Body.Variables[8]);
                ilLd.Emit(OpCodes.Ret);

                // Load track:
                // if (this._streams == null) return null;
                ilLd.Append(lblLoadTrack);
                ilLd.Emit(OpCodes.Ldfld, streamsField);
                var lblHasStreams = ilLd.Create(OpCodes.Ldarg_0);
                ilLd.Emit(OpCodes.Brtrue_S, lblHasStreams);
                ilLd.Emit(OpCodes.Ldnull);
                ilLd.Emit(OpCodes.Ret);

                // V_0 = this._streams[trackIndex];
                ilLd.Append(lblHasStreams);
                ilLd.Emit(OpCodes.Ldfld, streamsField);
                ilLd.Emit(OpCodes.Ldarg_1);
                ilLd.Emit(OpCodes.Ldelem_Any, streamInfoType);
                ilLd.Emit(OpCodes.Stloc_0);

                // V_1 = AudioEngine.OpenStream(this._waveBankFileName, false);
                ilLd.Emit(OpCodes.Ldarg_0);
                ilLd.Emit(OpCodes.Ldfld, fileNameField);
                ilLd.Emit(OpCodes.Ldc_I4_0); // useMemoryStream = false
                ilLd.Emit(OpCodes.Call, openStreamMethod);
                ilLd.Emit(OpCodes.Stloc_1);

                // if (V_1 == null) return null;
                ilLd.Emit(OpCodes.Ldloc_1);
                var lblOpenSuccess = ilLd.Create(OpCodes.Ldloc_1);
                ilLd.Emit(OpCodes.Brtrue_S, lblOpenSuccess);
                ilLd.Emit(OpCodes.Ldnull);
                ilLd.Emit(OpCodes.Ret);

                // try {
                //   V_1.Seek((long)(V_0.FileOffset + this._playRegionOffset), SeekOrigin.Begin);
                ilLd.Append(lblOpenSuccess);
                ilLd.Emit(OpCodes.Ldloc_0);
                ilLd.Emit(OpCodes.Ldfld, fileOffsetField);
                ilLd.Emit(OpCodes.Ldarg_0);
                ilLd.Emit(OpCodes.Ldfld, playRegionField);
                ilLd.Emit(OpCodes.Add);
                ilLd.Emit(OpCodes.Conv_I8);
                ilLd.Emit(OpCodes.Ldc_I4_0); // SeekOrigin.Begin
                ilLd.Emit(OpCodes.Callvirt, streamSeekMethod);
                ilLd.Emit(OpCodes.Pop);

                //   BinaryReader V_2 = new BinaryReader(V_1);
                ilLd.Emit(OpCodes.Ldloc_1);
                ilLd.Emit(OpCodes.Newobj, brCtor);
                ilLd.Emit(OpCodes.Stloc_2);

                //   byte[] V_3 = V_2.ReadBytes(V_0.FileLength);
                ilLd.Emit(OpCodes.Ldloc_2);
                ilLd.Emit(OpCodes.Ldloc_0);
                ilLd.Emit(OpCodes.Ldfld, fileLengthField);
                ilLd.Emit(OpCodes.Callvirt, brReadBytes);
                ilLd.Emit(OpCodes.Stloc_3);

                //   this.DecodeFormat(V_0.Format, out V_4, out V_5, out V_6, out V_7);
                ilLd.Emit(OpCodes.Ldarg_0);
                ilLd.Emit(OpCodes.Ldloc_0);
                ilLd.Emit(OpCodes.Ldfld, formatField);
                ilLd.Emit(OpCodes.Ldloca_S, loadSoundMethod.Body.Variables[4]);
                ilLd.Emit(OpCodes.Ldloca_S, loadSoundMethod.Body.Variables[5]);
                ilLd.Emit(OpCodes.Ldloca_S, loadSoundMethod.Body.Variables[6]);
                ilLd.Emit(OpCodes.Ldloca_S, loadSoundMethod.Body.Variables[7]);
                ilLd.Emit(OpCodes.Call, decodeFormatMethod);

                //   V_8 = new SoundEffect(V_4, V_3, V_5, V_6, V_7, V_0.LoopStart, V_0.LoopLength);
                ilLd.Emit(OpCodes.Ldloc_S, loadSoundMethod.Body.Variables[4]);
                ilLd.Emit(OpCodes.Ldloc_3);
                ilLd.Emit(OpCodes.Ldloc_S, loadSoundMethod.Body.Variables[5]);
                ilLd.Emit(OpCodes.Ldloc_S, loadSoundMethod.Body.Variables[6]);
                ilLd.Emit(OpCodes.Ldloc_S, loadSoundMethod.Body.Variables[7]);
                ilLd.Emit(OpCodes.Ldloc_0);
                ilLd.Emit(OpCodes.Ldfld, loopStartField);
                ilLd.Emit(OpCodes.Ldloc_0);
                ilLd.Emit(OpCodes.Ldfld, loopLengthField);
                ilLd.Emit(OpCodes.Newobj, seCtor7);
                ilLd.Emit(OpCodes.Stloc_S, loadSoundMethod.Body.Variables[8]);

                //   this._sounds[trackIndex] = V_8;
                ilLd.Emit(OpCodes.Ldarg_0);
                ilLd.Emit(OpCodes.Ldfld, soundsField);
                ilLd.Emit(OpCodes.Ldarg_1);
                ilLd.Emit(OpCodes.Ldloc_S, loadSoundMethod.Body.Variables[8]);
                ilLd.Emit(OpCodes.Stelem_Ref);

                // } finally {
                //   if (V_1 != null) ((IDisposable)V_1).Dispose();
                // }
                var lblLeave = ilLd.Create(OpCodes.Ldloc_S, loadSoundMethod.Body.Variables[8]);
                ilLd.Emit(OpCodes.Leave_S, lblLeave);

                // handler start
                var lblFinallyStart = ilLd.Create(OpCodes.Ldloc_1);
                ilLd.Append(lblFinallyStart);
                var lblEndFinally = ilLd.Create(OpCodes.Endfinally);
                ilLd.Emit(OpCodes.Brfalse_S, lblEndFinally);
                ilLd.Emit(OpCodes.Ldloc_1);
                ilLd.Emit(OpCodes.Callvirt, streamDisposeMethod);
                ilLd.Append(lblEndFinally);

                // return V_8;
                ilLd.Append(lblLeave);
                ilLd.Emit(OpCodes.Ret);

                // Add try-finally handler
                var tryStart = lblOpenSuccess;
                var tryEnd = lblFinallyStart;
                var handlerStart = lblFinallyStart;
                var handlerEnd = lblLeave;

                var eh = new ExceptionHandler(ExceptionHandlerType.Finally)
                {
                    TryStart = tryStart,
                    TryEnd = tryEnd,
                    HandlerStart = handlerStart,
                    HandlerEnd = handlerEnd
                };
                loadSoundMethod.Body.ExceptionHandlers.Add(eh);
                waveBankType.Methods.Add(loadSoundMethod);
                Console.WriteLine("Added WaveBank.LoadSoundEffect(int trackIndex) lazy loader.");

                // 5c. Patch GetSoundEffectInstance to use LoadSoundEffect and handle null with exception safety
                var getSEIMethod = waveBankType.Methods.FirstOrDefault(m => m.Name == "GetSoundEffectInstance");
                if (getSEIMethod != null)
                {
                    getSEIMethod.Body.Instructions.Clear();
                    getSEIMethod.Body.Variables.Clear();
                    getSEIMethod.Body.ExceptionHandlers.Clear();
                    getSEIMethod.Body.Variables.Add(new VariableDefinition(streamInfoType));
                    getSEIMethod.Body.Variables.Add(new VariableDefinition(soundEffectType));
                    getSEIMethod.Body.Variables.Add(new VariableDefinition(getPooledInstanceMethod.ReturnType));

                    var platformCreateStreamMethod = waveBankType.Methods.FirstOrDefault(m => m.Name == "PlatformCreateStream");

                    var ilSEI = getSEIMethod.Body.GetILProcessor();
                    var lblNonStreaming = ilSEI.Create(OpCodes.Ldarg_2);
                    var lblReturn = ilSEI.Create(OpCodes.Ldloc_2);

                    // if (this._streaming) {
                    ilSEI.Emit(OpCodes.Ldarg_0);
                    ilSEI.Emit(OpCodes.Ldfld, streamingField);
                    ilSEI.Emit(OpCodes.Brfalse_S, lblNonStreaming);

                    //   streaming = true;
                    ilSEI.Emit(OpCodes.Ldarg_2);
                    ilSEI.Emit(OpCodes.Ldc_I4_1);
                    ilSEI.Emit(OpCodes.Stind_I1);

                    //   StreamInfo stream = this._streams[trackIndex];
                    ilSEI.Emit(OpCodes.Ldarg_0);
                    ilSEI.Emit(OpCodes.Ldfld, streamsField);
                    ilSEI.Emit(OpCodes.Ldarg_1);
                    ilSEI.Emit(OpCodes.Ldelem_Any, streamInfoType);
                    ilSEI.Emit(OpCodes.Stloc_0);

                    //   return this.PlatformCreateStream(stream);
                    ilSEI.Emit(OpCodes.Ldarg_0);
                    ilSEI.Emit(OpCodes.Ldloc_0);
                    ilSEI.Emit(OpCodes.Call, platformCreateStreamMethod);
                    ilSEI.Emit(OpCodes.Ret);

                    // } else {
                    // try {
                    ilSEI.Append(lblNonStreaming);
                    //   streaming = false;
                    ilSEI.Emit(OpCodes.Ldc_I4_0);
                    ilSEI.Emit(OpCodes.Stind_I1);

                    //   SoundEffect se = this.LoadSoundEffect(trackIndex);
                    ilSEI.Emit(OpCodes.Ldarg_0);
                    ilSEI.Emit(OpCodes.Ldarg_1);
                    ilSEI.Emit(OpCodes.Call, loadSoundMethod);
                    ilSEI.Emit(OpCodes.Stloc_1);

                    //   if (se == null) { V_2 = null; leave lblReturn; }
                    var lblGetPooled = ilSEI.Create(OpCodes.Ldloc_1);
                    ilSEI.Emit(OpCodes.Ldloc_1);
                    ilSEI.Emit(OpCodes.Brtrue_S, lblGetPooled);
                    ilSEI.Emit(OpCodes.Ldnull);
                    ilSEI.Emit(OpCodes.Stloc_2);
                    ilSEI.Emit(OpCodes.Leave_S, lblReturn);

                    //   V_2 = se.GetPooledInstance(true);
                    ilSEI.Append(lblGetPooled);
                    ilSEI.Emit(OpCodes.Ldc_I4_1);
                    ilSEI.Emit(OpCodes.Callvirt, getPooledInstanceMethod);
                    ilSEI.Emit(OpCodes.Stloc_2);
                    ilSEI.Emit(OpCodes.Leave_S, lblReturn);

                    // } catch (Exception) {
                    var lblCatchStart = ilSEI.Create(OpCodes.Pop);
                    ilSEI.Append(lblCatchStart);
                    ilSEI.Emit(OpCodes.Ldnull);
                    ilSEI.Emit(OpCodes.Stloc_2);
                    ilSEI.Emit(OpCodes.Leave_S, lblReturn);

                    // return V_2;
                    ilSEI.Append(lblReturn);
                    ilSEI.Emit(OpCodes.Ret);

                    var catchHandler = new ExceptionHandler(ExceptionHandlerType.Catch)
                    {
                        CatchType = module.ImportReference(typeof(Exception)),
                        TryStart = lblNonStreaming,
                        TryEnd = lblCatchStart,
                        HandlerStart = lblCatchStart,
                        HandlerEnd = lblReturn
                    };
                    getSEIMethod.Body.ExceptionHandlers.Add(catchHandler);

                    Console.WriteLine("Patched WaveBank.GetSoundEffectInstance to use lazy SoundEffect loader with try/catch safeguard.");
                }

                // 5d. Patch WaveBank.Dispose(bool) to null-check elements before calling SoundEffect.Dispose()
                var disposeMethod = waveBankType.Methods.FirstOrDefault(m => m.Name == "Dispose" && m.Parameters.Count == 1);
                if (disposeMethod != null && !disposeMethod.Body.Instructions.Any(i => i.OpCode == OpCodes.Dup))
                {
                    // In Dispose(bool):
                    //   ldloc.0
                    //   ldloc.1
                    //   ldelem.ref
                    //   callvirt SoundEffect::Dispose()
                    for (int i = 0; i < disposeMethod.Body.Instructions.Count; i++)
                    {
                        var inst = disposeMethod.Body.Instructions[i];
                        if (inst.OpCode == OpCodes.Callvirt && inst.Operand is MethodReference mr && mr.Name == "Dispose")
                        {
                            var ldelemInst = disposeMethod.Body.Instructions[i - 1];
                            if (ldelemInst.OpCode == OpCodes.Ldelem_Ref)
                            {
                                // Dup element, check if null, if null pop and skip dispose call
                                var ilDisp = disposeMethod.Body.GetILProcessor();
                                var nextAfterDispose = disposeMethod.Body.Instructions[i + 1];
                                var lblSkip = nextAfterDispose;

                                var dupInst = ilDisp.Create(OpCodes.Dup);
                                var brnullInst = ilDisp.Create(OpCodes.Brfalse_S, lblSkip);
                                var popBeforeSkip = ilDisp.Create(OpCodes.Pop);

                                // Replace:
                                //   ldelem.ref
                                //   dup
                                //   brtrue.s lblDoDispose
                                //   pop
                                //   br.s lblSkip
                                // lblDoDispose:
                                //   callvirt Dispose
                                // lblSkip:
                                var lblDoDispose = inst;
                                var brtrueInst = ilDisp.Create(OpCodes.Brtrue_S, lblDoDispose);

                                ilDisp.InsertAfter(ldelemInst, dupInst);
                                ilDisp.InsertAfter(dupInst, brtrueInst);
                                ilDisp.InsertAfter(brtrueInst, popBeforeSkip);
                                ilDisp.InsertAfter(popBeforeSkip, ilDisp.Create(OpCodes.Br_S, lblSkip));

                                Console.WriteLine("Safeguarded WaveBank.Dispose(bool) against null lazily loaded sounds.");
                                break;
                            }
                        }
                    }
                }
            }
        }

        // 6. Neutralize broken AudioCategory.AddSound call in XactSound..ctor
        var xactSoundType = module.GetType("Microsoft.Xna.Framework.Audio.XactSound");
        if (xactSoundType != null)
        {
            var xsCtor3 = xactSoundType.Methods.FirstOrDefault(m => m.IsConstructor && m.Parameters.Count == 3 && m.Parameters[0].ParameterType.Name == "AudioEngine");
            if (xsCtor3 != null)
            {
                for (int i = 0; i < xsCtor3.Body.Instructions.Count - 1; i++)
                {
                    var inst = xsCtor3.Body.Instructions[i];
                    var next = xsCtor3.Body.Instructions[i + 1];
                    if (inst.OpCode == OpCodes.Ldarg_1 &&
                        next.OpCode == OpCodes.Callvirt &&
                        next.Operand.ToString().Contains("get_Categories"))
                    {
                        inst.OpCode = OpCodes.Ret;
                        inst.Operand = null;
                        while (xsCtor3.Body.Instructions.Count > i + 1)
                        {
                            xsCtor3.Body.Instructions.RemoveAt(i + 1);
                        }
                        Console.WriteLine("Neutralized broken AudioCategory.AddSound call in XactSound..ctor.");
                        break;
                    }
                }
            }
        }

        // 7. Safeguard AudioCategory with clean, non-allocating implementations
        var audioCategoryType = module.GetType("Microsoft.Xna.Framework.Audio.AudioCategory");
        if (audioCategoryType != null)
        {
            var catVolumeField = audioCategoryType.Fields.FirstOrDefault(f => f.Name == "_volume");

            // 7a. AudioCategory.AddSound: pure ret
            var addSoundMethod = audioCategoryType.Methods.FirstOrDefault(m => m.Name == "AddSound");
            if (addSoundMethod != null)
            {
                addSoundMethod.Body.Instructions.Clear();
                addSoundMethod.Body.ExceptionHandlers.Clear();
                addSoundMethod.Body.Variables.Clear();
                var ilAdd = addSoundMethod.Body.GetILProcessor();
                ilAdd.Emit(OpCodes.Ret);
                Console.WriteLine("Safeguarded AudioCategory.AddSound (pure no-op ret).");
            }

            // 7b. AudioCategory.SetVolume: cleanly set _volume[0] = volume
            var setVolMethod = audioCategoryType.Methods.FirstOrDefault(m => m.Name == "SetVolume");
            if (setVolMethod != null && catVolumeField != null)
            {
                var argExCtor = module.ImportReference(typeof(ArgumentException).GetConstructor(new[] { typeof(string) }));
                setVolMethod.Body.Instructions.Clear();
                setVolMethod.Body.ExceptionHandlers.Clear();
                setVolMethod.Body.Variables.Clear();
                var ilVol = setVolMethod.Body.GetILProcessor();
                var lblOk = ilVol.Create(OpCodes.Ldarg_0);

                // if (volume < 0.0f) throw new ArgumentException("The volume must be positive.");
                ilVol.Emit(OpCodes.Ldarg_1);
                ilVol.Emit(OpCodes.Ldc_R4, 0.0f);
                ilVol.Emit(OpCodes.Bge_Un_S, lblOk);
                ilVol.Emit(OpCodes.Ldstr, "The volume must be positive.");
                ilVol.Emit(OpCodes.Newobj, argExCtor);
                ilVol.Emit(OpCodes.Throw);

                // if (this._volume == null) this._volume = new float[1];
                ilVol.Append(lblOk);
                ilVol.Emit(OpCodes.Ldfld, catVolumeField);
                var lblHasVol = ilVol.Create(OpCodes.Ldarg_0);
                ilVol.Emit(OpCodes.Brtrue_S, lblHasVol);

                ilVol.Emit(OpCodes.Ldarg_0);
                ilVol.Emit(OpCodes.Ldc_I4_1);
                ilVol.Emit(OpCodes.Newarr, module.TypeSystem.Single);
                ilVol.Emit(OpCodes.Stfld, catVolumeField);

                // this._volume[0] = volume;
                ilVol.Append(lblHasVol);
                ilVol.Emit(OpCodes.Ldfld, catVolumeField);
                ilVol.Emit(OpCodes.Ldc_I4_0);
                ilVol.Emit(OpCodes.Ldarg_1);
                ilVol.Emit(OpCodes.Stelem_R4);
                ilVol.Emit(OpCodes.Ret);

                Console.WriteLine("Rebuilt AudioCategory.SetVolume cleanly with null check.");
            }

            // 7c. AudioCategory.Pause, Resume, Stop: pure ret
            foreach (var name in new[] { "Pause", "Resume", "Stop" })
            {
                var m = audioCategoryType.Methods.FirstOrDefault(meth => meth.Name == name);
                if (m != null)
                {
                    m.Body.Instructions.Clear();
                    m.Body.ExceptionHandlers.Clear();
                    m.Body.Variables.Clear();
                    var il = m.Body.GetILProcessor();
                    il.Emit(OpCodes.Ret);
                    Console.WriteLine($"Safeguarded AudioCategory.{name} (pure no-op ret).");
                }
            }

            // 7d. AudioCategory.GetPlayingInstanceCount: returns 0
            var getPlayingMethod = audioCategoryType.Methods.FirstOrDefault(m => m.Name == "GetPlayingInstanceCount");
            if (getPlayingMethod != null)
            {
                getPlayingMethod.Body.Instructions.Clear();
                getPlayingMethod.Body.ExceptionHandlers.Clear();
                getPlayingMethod.Body.Variables.Clear();
                var il = getPlayingMethod.Body.GetILProcessor();
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ret);
                Console.WriteLine("Safeguarded AudioCategory.GetPlayingInstanceCount (returns 0).");
            }

            // 7e. AudioCategory.GetOldestInstance: returns null
            var getOldestMethod = audioCategoryType.Methods.FirstOrDefault(m => m.Name == "GetOldestInstance");
            if (getOldestMethod != null)
            {
                getOldestMethod.Body.Instructions.Clear();
                getOldestMethod.Body.ExceptionHandlers.Clear();
                getOldestMethod.Body.Variables.Clear();
                var il = getOldestMethod.Body.GetILProcessor();
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Ret);
                Console.WriteLine("Safeguarded AudioCategory.GetOldestInstance (returns null).");
            }
        }

        assembly.Write();
        Console.WriteLine("Saved patched MonoGame.Framework.dll successfully.");
        return 0;
    }
}
