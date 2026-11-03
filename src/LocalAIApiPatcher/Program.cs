using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LocalAIApiPatcher
{
    /// <summary>
    /// IL-патчер для "Scam With Your Friends".
    ///
    /// Перенаправляет транспорт ИИ-запросов в Assembly-CSharp.dll и ScriptsAssDef.dll
    /// на локальный OpenAI-совместимый эндпоинт через
    /// LocalAIApiRuntime.CompleteChatCompletionAsync из LocalAIApiMod.dll.
    ///
    /// Пути не хардкодятся: каталог игры передаётся аргументом --managed.
    /// </summary>
    internal static class Program
    {
        private const string RuntimeTypeName = "LocalAIApiMod.LocalAIApiRuntime";
        private const string KolkataApiTypeName = "KolkataApi";
        private const string HostAiSettingsTypeName = "HostAiSettings";

        private static readonly string[] BooleanTrueGetters =
        {
            "get_BackendAuthenticated",
            "get_UsesDevelopmentBackend",
            "get_CanStartGame",
        };

        private static readonly string[] BooleanFalseGetters =
        {
            "get_BrowserSignInAvailable",
            "get_BrowserSignInPending",
            "get_ShowSignInSupport",
            "get_SystemClockIncorrect",
            "get_AiCreditsDepleted",
            "get_AiUsageRestricted",
        };

        private static int Main(string[] args)
        {
            try
            {
                var options = PatchOptions.Parse(args);

                if (!Directory.Exists(options.ManagedDir))
                    throw new DirectoryNotFoundException("Managed directory not found: " + options.ManagedDir);

                Directory.CreateDirectory(options.OutputDir);

                // Входные ассембли можно переопределить (--assembly-csharp/--scripts-assdef),
                // чтобы патчить из чистых бэкапов и не зависеть от текущего состояния Managed.
                var assemblyCSharpPath = options.AssemblyCSharpPath
                    ?? Path.Combine(options.ManagedDir, "Assembly-CSharp.dll");
                if (!File.Exists(assemblyCSharpPath))
                    throw new FileNotFoundException("Assembly-CSharp.dll not found: " + assemblyCSharpPath);

                var outputAssemblyCSharp = Path.Combine(options.OutputDir, "Assembly-CSharp.patched.dll");
                var outputScriptsAssDef = Path.Combine(options.OutputDir, "ScriptsAssDef.patched.dll");

                var resolver = new DefaultAssemblyResolver();
                resolver.AddSearchDirectory(options.ManagedDir);
                resolver.AddSearchDirectory(Path.GetDirectoryName(options.ModDllPath));

                var readerParameters = new ReaderParameters { AssemblyResolver = resolver };

                PatchAssemblyCSharp(readerParameters, assemblyCSharpPath, outputAssemblyCSharp, options);

                var scriptsAssDefPath = options.ScriptsAssDefPath
                    ?? Path.Combine(options.ManagedDir, "ScriptsAssDef.dll");
                PatchScriptsAssDef(readerParameters, scriptsAssDefPath, outputScriptsAssDef, options);

                Console.WriteLine("PATCH SUCCESS");
                Console.WriteLine("  " + outputAssemblyCSharp);
                Console.WriteLine("  " + outputScriptsAssDef);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("PATCH FAILED: " + ex.Message);
                Console.Error.WriteLine(ex);
                return 1;
            }
        }

        private static void PatchAssemblyCSharp(
            ReaderParameters readerParameters,
            string inputPath,
            string outputPath,
            PatchOptions options)
        {
            using var assembly = AssemblyDefinition.ReadAssembly(inputPath, readerParameters);
            using var modAssembly = AssemblyDefinition.ReadAssembly(options.ModDllPath, readerParameters);

            var module = assembly.MainModule;

            var runtimeType = modAssembly.MainModule.Types
                .FirstOrDefault(t => t.FullName == RuntimeTypeName)
                ?? throw new InvalidOperationException("Type " + RuntimeTypeName + " not found in " + options.ModDllPath);

            var ensureConfigMethod = runtimeType.Methods.FirstOrDefault(m => m.Name == "EnsureConfig")
                ?? throw new InvalidOperationException("EnsureConfig not found in " + options.ModDllPath);

            var completeChatMethod = runtimeType.Methods.FirstOrDefault(m => m.Name == "CompleteChatCompletionAsync")
                ?? throw new InvalidOperationException("CompleteChatCompletionAsync not found in " + options.ModDllPath);

            var ensureConfigRef = module.ImportReference(ensureConfigMethod);
            var completeChatRef = module.ImportReference(completeChatMethod);

            var kolkataApiType = module.Types.FirstOrDefault(t => t.Name == KolkataApiTypeName)
                ?? throw new InvalidOperationException(KolkataApiTypeName + " not found in " + inputPath);

            RedirectAiTransport(kolkataApiType, completeChatRef);
            ForceAuthenticationState(kolkataApiType);
            NeutralizeEdgeSession(kolkataApiType);
            InjectConfigBootstrap(kolkataApiType, ensureConfigRef);

            WriteAssembly(assembly, outputPath);
        }

        /// <summary>Перенаправляет KolkataApi::CompleteOpenRouterAsync на локальный транспорт.</summary>
        private static void RedirectAiTransport(TypeDefinition kolkataApiType, MethodReference completeChatRef)
        {
            var completeOpenRouter = kolkataApiType.Methods.FirstOrDefault(m => m.Name == "CompleteOpenRouterAsync")
                ?? throw new InvalidOperationException("CompleteOpenRouterAsync not found in " + kolkataApiType.FullName);

            ClearBody(completeOpenRouter);
            var il = completeOpenRouter.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldarg_1)); // JObject body
            il.Append(il.Create(OpCodes.Ldarg_2)); // CancellationToken
            il.Append(il.Create(OpCodes.Call, completeChatRef));
            il.Append(il.Create(OpCodes.Ret));

            Console.WriteLine("Hooked KolkataApi::CompleteOpenRouterAsync -> LocalAIApiRuntime.CompleteChatCompletionAsync");
        }

        /// <summary>Убирает зависимость запуска игры от backend-аутентификации Edge.</summary>
        private static void ForceAuthenticationState(TypeDefinition kolkataApiType)
        {
            foreach (var method in kolkataApiType.Methods)
            {
                if (method.Name == "get_BackendAuthStatus")
                {
                    ReplaceBodyWithInt(method, 2); // SignedIn
                    Console.WriteLine("Patched KolkataApi::get_BackendAuthStatus -> SignedIn");
                }
                else if (BooleanTrueGetters.Contains(method.Name))
                {
                    ReplaceBodyWithInt(method, 1);
                    Console.WriteLine("Patched KolkataApi::" + method.Name + " -> true");
                }
                else if (BooleanFalseGetters.Contains(method.Name))
                {
                    ReplaceBodyWithInt(method, 0);
                    Console.WriteLine("Patched KolkataApi::" + method.Name + " -> false");
                }
            }
        }

        /// <summary>Отключает реальные сетевые сессии Edge, которые больше не нужны.</summary>
        private static void NeutralizeEdgeSession(TypeDefinition kolkataApiType)
        {
            var startEdgeAuthentication = kolkataApiType.Methods.FirstOrDefault(m => m.Name == "StartEdgeAuthentication");
            if (startEdgeAuthentication != null && startEdgeAuthentication.HasBody)
            {
                ClearBody(startEdgeAuthentication);
                var il = startEdgeAuthentication.Body.GetILProcessor();
                il.Append(il.Create(OpCodes.Ret));
                Console.WriteLine("Patched KolkataApi::StartEdgeAuthentication -> no-op");
            }

            var maintainEdgeSession = kolkataApiType.Methods.FirstOrDefault(m => m.Name == "MaintainEdgeSessionAsync");
            if (maintainEdgeSession != null && maintainEdgeSession.HasBody)
            {
                ReplaceStructReturnWithDefault(maintainEdgeSession);
                Console.WriteLine("Patched KolkataApi::MaintainEdgeSessionAsync -> default(UniTaskVoid)");
            }

            var ensureEdgeSession = kolkataApiType.Methods.FirstOrDefault(m => m.Name == "EnsureEdgeSessionAsync");
            if (ensureEdgeSession != null && ensureEdgeSession.HasBody)
            {
                ReplaceStructReturnWithDefault(ensureEdgeSession);
                Console.WriteLine("Patched KolkataApi::EnsureEdgeSessionAsync -> default(UniTask)");
            }
        }

        /// <summary>Загружает конфиг мода до первого ИИ-запроса.</summary>
        private static void InjectConfigBootstrap(TypeDefinition kolkataApiType, MethodReference ensureConfigRef)
        {
            var awake = kolkataApiType.Methods.FirstOrDefault(m => m.Name == "Awake");
            if (awake == null || !awake.HasBody)
                return;

            var il = awake.Body.GetILProcessor();
            il.InsertBefore(awake.Body.Instructions[0], il.Create(OpCodes.Call, ensureConfigRef));
            Console.WriteLine("Injected LocalAIApiRuntime.EnsureConfig into KolkataApi::Awake");
        }

        private static void PatchScriptsAssDef(
            ReaderParameters readerParameters,
            string inputPath,
            string outputPath,
            PatchOptions options)
        {
            if (!File.Exists(inputPath))
            {
                Console.WriteLine("ScriptsAssDef.dll not found, skipping: " + inputPath);
                return;
            }

            using var assembly = AssemblyDefinition.ReadAssembly(inputPath, readerParameters);

            var hostAiSettingsType = assembly.MainModule.Types.FirstOrDefault(t => t.Name == HostAiSettingsTypeName);
            if (hostAiSettingsType == null)
            {
                Console.WriteLine("HostAiSettings not found, skipping ScriptsAssDef patch.");
                return;
            }

            foreach (var method in hostAiSettingsType.Methods)
            {
                switch (method.Name)
                {
                    case "get_OpenRouterSelected":
                    case "get_DirectSelected":
                    case "get_UseDirectOpenRouter":
                        ReplaceBodyWithInt(method, 1);
                        Console.WriteLine("Patched HostAiSettings::" + method.Name + " -> true");
                        break;

                    case "get_HasInvalidKey":
                        ReplaceBodyWithInt(method, 0);
                        Console.WriteLine("Patched HostAiSettings::get_HasInvalidKey -> false");
                        break;

                    case "get_ApiKey":
                        ReplaceBodyWithString(method, options.ApiKey);
                        Console.WriteLine("Patched HostAiSettings::get_ApiKey -> \"" + options.ApiKey + "\"");
                        break;
                }
            }

            WriteAssembly(assembly, outputPath);
            Console.WriteLine("Wrote " + outputPath);
        }

        private static void WriteAssembly(AssemblyDefinition assembly, string outputPath)
        {
            var tempPath = outputPath + ".tmp";
            assembly.Write(tempPath);

            if (File.Exists(outputPath))
                File.Delete(outputPath);

            File.Move(tempPath, outputPath);
        }

        private static void ClearBody(MethodDefinition method)
        {
            method.Body.Instructions.Clear();
            method.Body.Variables.Clear();
            method.Body.ExceptionHandlers.Clear();
        }

        private static void ReplaceBodyWithInt(MethodDefinition method, int value)
        {
            if (!method.HasBody)
                return;

            ClearBody(method);
            method.Body.InitLocals = false;

            var il = method.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldc_I4, value));
            il.Append(il.Create(OpCodes.Ret));
        }

        private static void ReplaceBodyWithString(MethodDefinition method, string value)
        {
            if (!method.HasBody)
                return;

            ClearBody(method);
            method.Body.InitLocals = false;

            var il = method.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldstr, value));
            il.Append(il.Create(OpCodes.Ret));
        }

        /// <summary>Возвращает default(T) для структурных return-типов (UniTask/UniTaskVoid).</summary>
        private static void ReplaceStructReturnWithDefault(MethodDefinition method)
        {
            var returnType = method.ReturnType;

            ClearBody(method);
            method.Body.InitLocals = true;

            var local = new VariableDefinition(returnType);
            method.Body.Variables.Add(local);

            var il = method.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldloca_S, local));
            il.Append(il.Create(OpCodes.Initobj, returnType));
            il.Append(il.Create(OpCodes.Ldloc_0));
            il.Append(il.Create(OpCodes.Ret));
        }

        private sealed class PatchOptions
        {
            public string ManagedDir { get; private set; } = string.Empty;
            public string OutputDir { get; private set; } = string.Empty;
            public string ModDllPath { get; private set; } = string.Empty;
            public string ApiKey { get; private set; } = "magpie";
            public string? AssemblyCSharpPath { get; private set; }
            public string? ScriptsAssDefPath { get; private set; }

            public static PatchOptions Parse(string[] args)
            {
                var options = new PatchOptions();

                for (var i = 0; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "--managed":
                            options.ManagedDir = RequireValue(args, ++i, "--managed");
                            break;
                        case "--output":
                            options.OutputDir = RequireValue(args, ++i, "--output");
                            break;
                        case "--mod":
                            options.ModDllPath = RequireValue(args, ++i, "--mod");
                            break;
                        case "--api-key":
                            options.ApiKey = RequireValue(args, ++i, "--api-key");
                            break;
                        case "--assembly-csharp":
                            options.AssemblyCSharpPath = RequireValue(args, ++i, "--assembly-csharp");
                            break;
                        case "--scripts-assdef":
                            options.ScriptsAssDefPath = RequireValue(args, ++i, "--scripts-assdef");
                            break;
                        case "--help":
                        case "-h":
                            PrintUsage();
                            Environment.Exit(0);
                            break;
                        default:
                            throw new ArgumentException("Unknown argument: " + args[i]);
                    }
                }

                if (string.IsNullOrWhiteSpace(options.OutputDir))
                    options.OutputDir = Path.Combine(Environment.CurrentDirectory, "build");

                if (string.IsNullOrWhiteSpace(options.ModDllPath))
                    options.ModDllPath = Path.Combine(options.OutputDir, "LocalAIApiMod.dll");

                if (string.IsNullOrWhiteSpace(options.ManagedDir))
                {
                    PrintUsage();
                    throw new ArgumentException("--managed is required.");
                }

                options.ManagedDir = Path.GetFullPath(options.ManagedDir);
                options.OutputDir = Path.GetFullPath(options.OutputDir);
                options.ModDllPath = Path.GetFullPath(options.ModDllPath);

                if (!string.IsNullOrWhiteSpace(options.AssemblyCSharpPath))
                    options.AssemblyCSharpPath = Path.GetFullPath(options.AssemblyCSharpPath);

                if (!string.IsNullOrWhiteSpace(options.ScriptsAssDefPath))
                    options.ScriptsAssDefPath = Path.GetFullPath(options.ScriptsAssDefPath);

                return options;
            }

            private static string RequireValue(IReadOnlyList<string> args, int index, string name)
            {
                if (index >= args.Count)
                    throw new ArgumentException("Missing value for " + name);

                return args[index];
            }

            private static void PrintUsage()
            {
                Console.WriteLine("Usage:");
                Console.WriteLine("  LocalAIApiPatcher --managed <ManagedDir> [options]");
                Console.WriteLine();
                Console.WriteLine("Options:");
                Console.WriteLine("  --output <OutDir>          Каталог для *.patched.dll (по умолчанию .\\build)");
                Console.WriteLine("  --mod <LocalAIApiMod.dll>  Рантайм мода (по умолчанию <OutDir>\\LocalAIApiMod.dll)");
                Console.WriteLine("  --api-key <key>            Ключ, прошиваемый в HostAiSettings::get_ApiKey (по умолчанию magpie)");
                Console.WriteLine("  --assembly-csharp <path>   Принять Assembly-CSharp.dll из другого файла (например, чистый .bak)");
                Console.WriteLine("  --scripts-assdef <path>    Принять ScriptsAssDef.dll из другого файла");
                Console.WriteLine();
                Console.WriteLine("Example:");
                Console.WriteLine("  LocalAIApiPatcher --managed \"C:\\Game\\Game_Data\\Managed\" --output .\\build");
            }
        }
    }
}
