using Azure;
using Azure.Data.Tables;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using EchoBot.Api.Constants;
using EchoBot.Api.Controllers;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.Extensions.Logging;
using Microsoft.Skype.Bots.Media;
using Newtonsoft.Json;
using System.Runtime.InteropServices;
using System.Text;

namespace EchoBot.Api.Media
{
    /// <summary>
    /// Class CognitiveServicesService.
    /// </summary>
    public class CognitiveServicesService
    {
        /// <summary>
        /// The is the indicator if the media stream is running
        /// </summary>
        private bool _isRunning = false;
        /// <summary>
        /// The is draining indicator
        /// </summary>
        protected bool _isDraining;

        /// <summary>
        /// The logger
        /// </summary>
        private readonly ILogger _logger;
        private readonly PushAudioInputStream _audioInputStream = AudioInputStream.CreatePushStream(AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1));
        private readonly AudioOutputStream _audioOutputStream = AudioOutputStream.CreatePullStream();

        private readonly SpeechConfig _speechConfig;
        private SpeechRecognizer _recognizer;
        private readonly SpeechSynthesizer _synthesizer;
        private readonly EventHubProducerClient _producerClient;
        private readonly Dictionary<string, string> eventHubConnectionsDict = new Dictionary<string, string>();
        private readonly string _threadId;
        private readonly string _botInstanceBaseUrl;
        /// <summary>
        /// Initializes a new instance of the <see cref="CognitiveServicesService" /> class.
        public CognitiveServicesService(string callTenantId, string callThreadId, AppSettings settings, ILogger logger)
        {
            _logger = logger;

            _speechConfig = SpeechConfig.FromSubscription(settings.SpeechConfigKey, settings.SpeechConfigRegion);
            _speechConfig.SpeechSynthesisLanguage = settings.BotLanguage;
            _speechConfig.SpeechRecognitionLanguage = settings.BotLanguage;

            _threadId = callThreadId;

            _botInstanceBaseUrl = $"https://{settings.ServiceDnsName}:{settings.BotInstanceExternalPort}";

            //_speechConfig.SpeechSynthesisVoiceName = "en-US-JennyNeural";

            var audioConfig = AudioConfig.FromStreamOutput(_audioOutputStream);
            _synthesizer = new SpeechSynthesizer(_speechConfig, audioConfig);

            var serviceClient = new TableServiceClient(settings.StorageConnectionString);

            // Create the table client.
            var tableClient = serviceClient.GetTableClient(AppConstants.EventHubConnectionsTableName);

            Pageable<TableEntity> queryResultsSelect = tableClient.Query<TableEntity>(select: new List<string>() { "ConnectionString", "PartitionKey", "RowKey" });
            foreach (TableEntity qEntity in queryResultsSelect)
            {
                var tenantId = qEntity.GetString("PartitionKey");
                var connectionString = qEntity.GetString("ConnectionString");
                eventHubConnectionsDict.Add(tenantId, connectionString);
            }

            if (!eventHubConnectionsDict.TryGetValue(callTenantId, out string eventHubConnectionString))
            {
                throw new Exception("Unable to find an event hub connection string");
            }

            _producerClient = new EventHubProducerClient(eventHubConnectionString);
        }

        /// <summary>
        /// Appends the audio buffer.
        /// </summary>
        /// <param name="audioBuffer"></param>
        public async Task AppendAudioBuffer(AudioMediaBuffer audioBuffer)
        {
            if (!_isRunning)
            {
                Start();
                await ProcessSpeech();
            }

            try
            {
                // audio for a 1:1 call
                var bufferLength = audioBuffer.Length;
                if (bufferLength > 0)
                {
                    var buffer = new byte[bufferLength];
                    Marshal.Copy(audioBuffer.Data, buffer, 0, (int)bufferLength);

                    _audioInputStream.Write(buffer);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Exception happend writing to input stream");
            }
        }

        public async Task TextToSpeech(SynthesizerRequest req)
        {
            var voiceName = string.IsNullOrEmpty(req.VoiceInfo.Name) ? "en-US-JennyNeural" : req.VoiceInfo.Name;
            var role = string.IsNullOrEmpty(req.VoiceInfo.Role) ? "default" : req.VoiceInfo.Role;
            var style = string.IsNullOrEmpty(req.VoiceInfo.Style) ? "default" : req.VoiceInfo.Style;
            var styleDegree = req.VoiceInfo.StyleDegree ?? 1;
            //<mstts:express-as role='{role}' style='{style}' styledegree='{styleDegree}'>
            var ssml = @$"<speak version='1.0' xml:lang='en-US' xmlns='http://www.w3.org/2001/10/synthesis' xmlns:mstts='http://www.w3.org/2001/mstts'>
                <voice name='{voiceName}'>
                    <mstts:express-as style='{style}'>
                        {req.Message}
                    </mstts:express-as>
                </voice>
            </speak>";


            // convert the text to speech
            var result = await _synthesizer.SpeakSsmlAsync(ssml);
            //SpeechSynthesisResult result = await _synthesizer.SpeakTextAsync(text);
            // take the stream of the result
            // create 20ms media buffers of the stream
            // and send to the AudioSocket in the BotMediaStream
            using (var stream = AudioDataStream.FromResult(result))
            {
                var currentTick = DateTime.Now.Ticks;
                MediaStreamEventArgs args = new MediaStreamEventArgs
                {
                    AudioMediaBuffers = Util.Utilities.CreateAudioMediaBuffers(stream, currentTick, _logger)
                };
                OnSendMediaBufferEventArgs(this, args);
            }
        }

        public virtual void OnSendMediaBufferEventArgs(object sender, MediaStreamEventArgs e)
        {
            if (SendMediaBuffer != null)
            {
                SendMediaBuffer(this, e);
            }
        }

        public event EventHandler<MediaStreamEventArgs> SendMediaBuffer;

        /// <summary>
        /// Ends this instance.
        /// </summary>
        /// <returns>Task.</returns>
        public async Task ShutDownAsync()
        {
            if (!_isRunning)
            {
                return;
            }

            if (_isRunning)
            {
                await _recognizer.StopContinuousRecognitionAsync();
                _recognizer.Dispose();
                _audioInputStream.Dispose();
                _audioOutputStream.Dispose();
                _synthesizer.Dispose();
                await _producerClient.DisposeAsync();

                _isRunning = false;
            }
        }

        /// <summary>
        /// Starts this instance.
        /// </summary>
        private void Start()
        {
            if (!_isRunning)
            {
                _isRunning = true;
            }
        }

        /// <summary>
        /// Processes this instance.
        /// </summary>
        private async Task ProcessSpeech()
        {
            try
            {
                var stopRecognition = new TaskCompletionSource<int>();

                using (var audioInput = AudioConfig.FromStreamInput(_audioInputStream))
                {
                    if (_recognizer == null)
                    {
                        _logger.LogInformation("init recognizer");
                        _recognizer = new SpeechRecognizer(_speechConfig, audioInput);
                    }
                }

                _recognizer.Recognizing += (s, e) =>
                {
                    _logger.LogInformation($"RECOGNIZING: Text={e.Result.Text}");
                };

                _recognizer.Recognized += async (s, e) =>
                {
                    if (e.Result.Reason == ResultReason.RecognizedSpeech)
                    {
                        if (string.IsNullOrEmpty(e.Result.Text))
                            return;

                        _logger.LogInformation($"RECOGNIZED: Text={e.Result.Text}");
                        // We recognized the speech
                        // Now do Speech to Text
                        //await TextToSpeech(e.Result.Text);
                        await PublishRecognizedText(e.Result.Text);
                    }
                    else if (e.Result.Reason == ResultReason.NoMatch)
                    {
                        _logger.LogInformation($"NOMATCH: Speech could not be recognized.");
                    }
                };

                _recognizer.Canceled += (s, e) =>
                {
                    _logger.LogInformation($"CANCELED: Reason={e.Reason}");

                    if (e.Reason == CancellationReason.Error)
                    {
                        _logger.LogInformation($"CANCELED: ErrorCode={e.ErrorCode}");
                        _logger.LogInformation($"CANCELED: ErrorDetails={e.ErrorDetails}");
                        _logger.LogInformation($"CANCELED: Did you update the subscription info?");
                    }

                    stopRecognition.TrySetResult(0);
                };

                _recognizer.SessionStarted += async (s, e) =>
                {
                    _logger.LogInformation("\nSession started event.");
                };

                _recognizer.SessionStopped += (s, e) =>
                {
                    _logger.LogInformation("\nSession stopped event.");
                    _logger.LogInformation("\nStop recognition.");
                    stopRecognition.TrySetResult(0);
                };

                // Starts continuous recognition. Uses StopContinuousRecognitionAsync() to stop recognition.
                await _recognizer.StartContinuousRecognitionAsync().ConfigureAwait(false);

                // Waits for completion.
                // Use Task.WaitAny to keep the task rooted.
                Task.WaitAny(new[] { stopRecognition.Task });

                // Stops recognition.
                await _recognizer.StopContinuousRecognitionAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException ex)
            {
                _logger.LogError(ex, "The queue processing task object has been disposed.");
            }
            catch (Exception ex)
            {
                // Catch all other exceptions and log
                _logger.LogError(ex, "Caught Exception");
            }

            _isDraining = false;
        }

        private async Task PublishRecognizedText(string text)
        {
            var transcript = new Transcript
            {
                BotInstanceUrl = _botInstanceBaseUrl,
                MeetingId = _threadId,
                Message = text
            };

            var json = JsonConvert.SerializeObject(transcript);

            // Create a batch of events 
            using EventDataBatch eventBatch = await _producerClient.CreateBatchAsync();
            if (!eventBatch.TryAdd(new EventData(json)))
            {
                // if it is too large for the batch
                _logger.LogError($"Failed to add recognized text to the event data batch. Text: ${text}");
            }

            try
            {
                // Use the producer client to send the batch of events to the event hub
                await _producerClient.SendAsync(eventBatch);
            }
            catch(Exception ex)
            {
                _logger.LogError(ex, $"Failed to send recognized text to the event hub. Text: ${text}");
            }
        }

        public class Transcript
        {
            public string BotInstanceUrl { get; set; }
            public string MeetingId { get; set; }

            public string Message { get; set; }
        }
    }
}
