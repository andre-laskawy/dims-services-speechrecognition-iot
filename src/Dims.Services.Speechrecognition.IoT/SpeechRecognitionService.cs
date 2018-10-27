///-----------------------------------------------------------------
///   File:         SpeechRecognizer.cs
///   Author:   	Andre Laskawy           
///   Date:         13.10.2018 10:02:20
///-----------------------------------------------------------------

namespace Dims.Services.Speechrecognition.IoT
{
    using Nanomite.Core.Network;
    using Nanomite.Core.Network.Common;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;
    using Windows.Globalization;
    using Windows.Media.SpeechRecognition;

    /// <summary>
    /// Defines the <see cref="SpeechRecognitionService" />
    /// </summary>
    public sealed class SpeechRecognitionService
    {
        /// <summary>
        /// The client
        /// </summary>
        private static NanomiteClient client;

        /// <summary>
        /// The recognizer
        /// </summary>
        private static SpeechRecognizer recognizer;

        /// <summary>
        /// The service guard
        /// </summary>
        private static Timer serviceGuard;

        /// <summary>
        /// The is processing input flag
        /// </summary>
        private static bool IsProcessingInput;

        /// <summary>
        /// The hotword
        /// </summary>
        public static string Hotword { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="SpeechRecognitionService"/> class.
        /// </summary>
        /// <param name="hotword">The hotword.</param>
        public static void Run(string brokerAddress, string user, string pass, string secret, string hotword)
        {
            Hotword = hotword;

            /// Init connection to broker
            InitBrokerConnection(brokerAddress, user, pass, secret);

            // Reinit speechrecognition to ensure it is running forever
            serviceGuard = new Timer(Run, null, 1000, 1000 * 60);
        }

        /// <summary>
        /// Initializes the connection.
        /// </summary>
        /// <param name="config">The configuration.</param>
        /// <returns></returns>
        private static async void InitBrokerConnection(string brokerAddress, string user, string pass, string secret)
        {
            try
            {
                client = NanomiteClient.CreateGrpcClient(brokerAddress, user);
                client.OnConnected = () => { Debug.WriteLine("Connected"); };
                await client.ConnectAsync(user, pass, secret);
            }
            catch(Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }

        /// <summary>
        /// Runs the service.
        /// </summary>
        private static async void Run(object state)
        {
            try
            {
                if (IsProcessingInput)
                {
                    return;
                }

                if (recognizer != null)
                {
                    await recognizer.ContinuousRecognitionSession.StopAsync();
                    recognizer.Dispose();
                }

                recognizer = new SpeechRecognizer(new Language("de-DE"));
                recognizer.StateChanged += RecognizerStateChanged;
                recognizer.ContinuousRecognitionSession.ResultGenerated += RecognizerResultGenerated;

                var textGrammar = new SpeechRecognitionListConstraint(new List<string> { "Licht an", "Licht aus" });
                var webSearchGrammar = new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.WebSearch, "webSearch");
                recognizer.Constraints.Add(textGrammar);
                recognizer.Constraints.Add(webSearchGrammar);
                SpeechRecognitionCompilationResult compilationResult = await recognizer.CompileConstraintsAsync();

                if (compilationResult.Status == SpeechRecognitionResultStatus.Success)
                {
                    Debug.WriteLine("Result: " + compilationResult.ToString());
                    await Listen();
                }
                else
                {
                    Debug.WriteLine("Status: " + compilationResult.Status);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }

        /// <summary>
        /// Start listening
        /// </summary>
        /// <returns></returns>
        private static async Task Listen()
        {
            try
            {
                if (recognizer.State == SpeechRecognizerState.Idle)
                {
                    await recognizer.ContinuousRecognitionSession.StartAsync();
                }
            }
            catch (System.Runtime.InteropServices.COMException e) when (e.HResult == unchecked((int)0x80045509))
            {
                Debug.WriteLine("Policy error");
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }

        /// <summary>
        /// Event if speech was detected
        /// </summary>
        /// <param name="session">The session.</param>
        /// <param name="args">The <see cref="Windows.Media.SpeechRecognition.SpeechContinuousRecognitionResultGeneratedEventArgs" /> instance containing the event data.</param>
        private static void RecognizerResultGenerated(SpeechContinuousRecognitionSession session, SpeechContinuousRecognitionResultGeneratedEventArgs args)
        {
            if (!string.IsNullOrEmpty(args.Result.Text)) 
            {
                if (args.Result.Confidence == SpeechRecognitionConfidence.High
                    || args.Result.Confidence == SpeechRecognitionConfidence.Medium)
                {
                    IsProcessingInput = true;
                    Debug.WriteLine("User input: " + args.Result.Text);

                    try
                    {
                        if (client != null)
                        {
                            if (args.Result.Text == Hotword)
                            {
                                Publish(Hotword);
                            }
                            else
                            {
                                if (args.Result.Text.ToLower().Contains("licht an"))
                                {
                                    Publish("LivingRoomLightOn");
                                }
                                else if (args.Result.Text.ToLower().Contains("licht aus"))
                                {
                                    Publish("LivingRoomLightOff");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(ex);
                    }
                    finally
                    {
                        IsProcessingInput = false;
                    }
                }
            }
        }

        /// <summary>
        /// Event if recognizer state changed.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="args">The <see cref="Windows.Media.SpeechRecognition.SpeechRecognizerStateChangedEventArgs" /> instance containing the event data.</param>
        private static async void RecognizerStateChanged(Windows.Media.SpeechRecognition.SpeechRecognizer sender, SpeechRecognizerStateChangedEventArgs args)
        {
            Debug.WriteLine("Speech recognizer state: " + args.State);
            if (args.State == SpeechRecognizerState.Idle && !IsProcessingInput)
            {
                await Listen();
            }
        }

        /// <summary>
        /// Publishes the specified topic.
        /// </summary>
        /// <param name="topic">The topic.</param>
        private static async void Publish(string topic)
        {
            var cmd = new Command() { Type = CommandType.Action, Topic = topic };
            await client.SendCommandAsync(cmd);
        }
    }
}