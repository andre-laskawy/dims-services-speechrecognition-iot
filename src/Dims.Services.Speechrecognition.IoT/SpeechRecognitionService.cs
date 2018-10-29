///-----------------------------------------------------------------
///   File:         SpeechRecognizer.cs
///   Author:   	Andre Laskawy           
///   Date:         13.10.2018 10:02:20
///-----------------------------------------------------------------

namespace Dims.Services.Speechrecognition.IoT
{
    using Dims.Common.Models;
    using Google.Protobuf.WellKnownTypes;
    using Grpc.Core.Logging;
    using Nanomite;
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
        /// The last listing cycle.
        /// </summary>
        private static DateTime lastListenCylce = DateTime.MinValue;

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
                await client.ConnectAsync(user, pass, secret, true);
            }
            catch(Exception ex)
            {
                throw ex;
            }
        }

        /// <summary>
        /// Runs the service.
        /// </summary>
        private static async void Run(object state)
        {
            try
            {
                // restart listener if nothing has happend for more than 30 seconds
                if (lastListenCylce > DateTime.Now.AddSeconds(-30))
                {
                    return;
                }

                if (recognizer != null)
                {
                    try
                    {
                        await recognizer.StopRecognitionAsync();
                    }
                    catch(Exception ex)
                    {
                        Log(ex);
                    }
                }

                recognizer = new SpeechRecognizer(new Language("de-DE"));
                recognizer.Timeouts.InitialSilenceTimeout = TimeSpan.FromSeconds(2);
                recognizer.Timeouts.EndSilenceTimeout = TimeSpan.FromSeconds(0.5);
                recognizer.StateChanged += RecognizerStateChanged;
                recognizer.ContinuousRecognitionSession.ResultGenerated += RecognizerResultGenerated;

                var textGrammar = new SpeechRecognitionListConstraint(new List<string> { "Licht an", "Licht aus" });
                var webSearchGrammar = new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.WebSearch, "webSearch");
                recognizer.Constraints.Add(textGrammar);
                recognizer.Constraints.Add(webSearchGrammar);
                SpeechRecognitionCompilationResult compilationResult = await recognizer.CompileConstraintsAsync();

                if (compilationResult.Status == SpeechRecognitionResultStatus.Success)
                {
                    Log(LogLevel.Debug, "Speechrecognition compile result: " + compilationResult.ToString());
                    await Listen();
                }
                else
                {
                    Log(LogLevel.Debug, "Speechrecognition compile result: " + compilationResult.ToString());
                }
            }
            catch (Exception ex)
            {
                Log(ex);
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
                Log(LogLevel.Warning, "Policy error");
            }
            catch (Exception ex)
            {
                Log(ex);
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
                    Log(LogLevel.Debug, "User input recognized: " + args.Result.Text);
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
                        Log(ex);
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
            Log(LogLevel.Debug, "Speech recognizer state: " + args.State);
            if (args.State == SpeechRecognizerState.Idle)
            {
                lastListenCylce = DateTime.Now;
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

        private static async void Log(LogLevel level, string message)
        {
            Debug.WriteLine(message);
            var cmd = new Command() { Type = CommandType.Action, Topic = level.ToString() };
            LogMessage logMessage = new LogMessage()
            {
                Level = level.ToString(),
                Message = message
            };
            cmd.Data.Add(Any.Pack(logMessage));

            await client.SendCommandAsync(cmd);
        }
        
        private static async void Log(Exception ex)
        {
            Debug.WriteLine(ex);
            var cmd = new Command() { Type = CommandType.Action, Topic = LogLevel.Error.ToString() };
            LogMessage logMessage = new LogMessage()
            {
                Level = LogLevel.Error.ToString(),
                Message = ex.ToText(),
                StackTrace = ex.StackTrace
            };
            cmd.Data.Add(Any.Pack(logMessage));

            await client.SendCommandAsync(cmd);
        }
    }
}