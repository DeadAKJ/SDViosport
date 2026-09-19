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
                il.Emit(OpCodes.Ldloc_1);
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

                Console.WriteLine("Patched TitleContainer.PlatformOpenStream with multi-path resolution (Location -> Documents -> Documents/StardewValley -> App Bundle -> Fallback).");
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

        assembly.Write();
        Console.WriteLine("Saved patched MonoGame.Framework.dll successfully.");
        return 0;
    }
}
