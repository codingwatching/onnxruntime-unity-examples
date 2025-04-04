/// Copyright (c) Microsoft Corporation. All rights reserved.
/// Licensed under the MIT License.
/// 
/// Modified by @asus4

using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntimeGenAI;
using Microsoft.ML.OnnxRuntime.Unity;
using UnityEngine;


namespace Microsoft.ML.OnnxRuntime.Examples
{
    /// <summary>
    /// PHI4 Multi Model Inference
    /// 
    /// Ported from C# example in GenAI repo
    /// https://github.com/microsoft/onnxruntime-genai/blob/cb5baa7f5cefa6a8cb804de634280d400d090729/examples/csharp/HelloPhi4MM/Program.cs
    /// </summary>
    public sealed class Phi4MultiModal : IDisposable
    {
        readonly Config config;
        readonly Model model;
        readonly Tokenizer tokenizer;


        bool disposed = false;

        public Phi4MultiModal(string modelPath, string provider)
        {
            // Set ORT_LIB_PATH environment variable to use GenAI
            OrtUnityEnv.InitializeOrtLibPath();

            // FIXME: Disabling provider for testing
            provider = string.Empty;
            if (string.IsNullOrWhiteSpace(provider))
            {
                model = new Model(modelPath);
            }
            else
            {
                // TODO: Test on Windows / Linux
                config = new Config(modelPath);
                config.ClearProviders();
                config.AppendProvider(provider);
                if (provider.Equals("cuda"))
                {
                    config.SetProviderOption(provider, "enable_cuda_graph", "0");
                }
            }
            tokenizer = new Tokenizer(model);
        }

        ~Phi4MultiModal()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
        }

        void Dispose(bool disposing)
        {
            if (disposed)
            {
                return;
            }
            if (disposing)
            {
                tokenizer?.Dispose();
                model?.Dispose();
                config?.Dispose();
            }
            disposed = true;
        }

        public static async Awaitable<Phi4MultiModal> InitAsync(string modelPath, string providerName, CancellationToken cancellationToken)
        {
            if (Debug.isDebugBuild)
            {
                // Verbose GenAI Log
                Environment.SetEnvironmentVariable("ORTGENAI_LOG_ORT_LIB", "1");
            }

            if (!Directory.Exists(modelPath))
            {
                string msg = $"Model not found at {modelPath}, download it from HuggingFace https://huggingface.co/microsoft";
                Debug.LogError(msg);
                throw new DirectoryNotFoundException(msg);
            }

            // Run in BG thread to avoid blocking the Unity thread
            await Awaitable.BackgroundThreadAsync();
            cancellationToken.ThrowIfCancellationRequested();

            Phi4MultiModal instance = null;
            try
            {
                instance = new Phi4MultiModal(modelPath, providerName);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to create Phi4MultiModal: {ex.Message}");
                instance?.Dispose();
                throw ex;
            }

            await Awaitable.MainThreadAsync();
            cancellationToken.ThrowIfCancellationRequested();

            return instance;
        }

        public async IAsyncEnumerable<string> GenerateStream(
            string prompt,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Awaitable.BackgroundThreadAsync();
            cancellationToken.ThrowIfCancellationRequested();

            using var sequences = tokenizer.Encode($"<|user|>{prompt}<|end|><|assistant|>");
            using var tokenizerStream = tokenizer.CreateStream();

            const int minLength = 50;
            const int maxLength = 500;

            using var generatorParams = new GeneratorParams(model);
            generatorParams.SetSearchOption("min_length", minLength);
            generatorParams.SetSearchOption("max_length", maxLength);
            using var generator = new Generator(model, generatorParams);

            generator.AppendTokenSequences(sequences);

            // Return results in the Unity main thread
            await Awaitable.MainThreadAsync();

            var outputQueue = new ConcurrentQueue<string>();
            var generateTask = Task.Run(() =>
            {
                while (!generator.IsDone())
                {
                    generator.GenerateNextToken();
                    outputQueue.Enqueue(tokenizerStream.Decode(generator.GetSequence(0)[^1]));
                }
            }, cancellationToken);

            while (!generateTask.IsCompleted)
            {
                await Awaitable.NextFrameAsync(cancellationToken);
                if (outputQueue.TryDequeue(out var response))
                {
                    yield return response;
                }
            }
        }
    }
}
