﻿using System.Collections.Concurrent;
using System.Net;
using EchoBot.Api.Authentication;
using EchoBot.Api.Constants;
using EchoBot.Api.Models;
using EchoBot.Api.Util;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Graph.Communications.Client;
using Microsoft.Graph.Communications.Common;
using Microsoft.Graph.Communications.Common.Telemetry;
using Microsoft.Graph.Communications.Resources;
using Microsoft.Skype.Bots.Media;

namespace EchoBot.Api.Bot
{
    /// <summary>
    /// Class BotService.
    /// Implements the <see cref="System.IDisposable" />
    /// Implements the <see cref="EchoBot.Services.Contract.IBotService" />
    /// </summary>
    /// <seealso cref="System.IDisposable" />
    /// <seealso cref="EchoBot.Services.Contract.IBotService" />
    public class BotService : IDisposable, IBotService
    {
        /// <summary>
        /// The Graph logger
        /// </summary>
        private readonly IGraphLogger _graphLogger;

        /// <summary>
        /// The logger
        /// </summary>
        private readonly ILogger _logger;

        /// <summary>
        /// The settings
        /// </summary>
        private readonly AppSettings _settings;
        private readonly IBotMediaLogger _mediaPlatformLogger;

        //private readonly AzureSettings _azureSettings;

        /// <summary>
        /// Gets the collection of call handlers.
        /// </summary>
        /// <value>The call handlers.</value>
        public ConcurrentDictionary<string, CallHandler> CallHandlers { get; } = new ConcurrentDictionary<string, CallHandler>();

        /// <summary>
        /// Gets the entry point for stateful bot.
        /// </summary>
        /// <value>The client.</value>
        public ICommunicationsClient Client { get; private set; }



        /// <inheritdoc />
        public void Dispose()
        {
            this.Client?.Dispose();
            this.Client = null;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="BotService" /> class.

        /// </summary>
        /// <param name="logger">The logger.</param>
        /// <param name="eventPublisher">The event publisher.</param>
        /// <param name="settings">The settings.</param>
        public BotService(
            IGraphLogger graphLogger,
            ILogger<BotService> logger,
            IOptions<AppSettings> settings,
            IBotMediaLogger mediaLogger)
        {
            _graphLogger = graphLogger;
            _logger = logger;
            _settings = settings.Value;
            //_azureSettings = (AzureSettings)azureSettings;
            _mediaPlatformLogger = mediaLogger;
        }

        /// <summary>
        /// Initialize the instance.
        /// </summary>
        public void Initialize()
        {
            _logger.LogInformation("Initializing Bot Service");
            var name = this.GetType().Assembly.GetName().Name;
            var builder = new CommunicationsClientBuilder(
                name,
                _settings.AadAppId,
                _graphLogger);

            var authProvider = new AuthenticationProvider(
                name,
                _settings.AadAppId,
                _settings.AadAppSecret,
                _graphLogger);
            //_logger);

            var botInternalHostingProtocol = "https";
            if (_settings.UseLocalDevSettings)
            {
                // if running locally with ngrok
                // the call signalling and notification will use the same internal and external ports
                // because you cannot receive requests on the same tunnel with different ports

                // calls come in over 443 (external) and route to the internally hosted port: BotCallingInternalPort
                _settings.BotInstanceExternalPort = 443;
                _settings.BotInternalPort = _settings.BotCallingInternalPort;
                botInternalHostingProtocol = "http";

                if (string.IsNullOrEmpty(_settings.MediaDnsName)) throw new ArgumentNullException(nameof(_settings.MediaDnsName));
            }
            else
            {
                _settings.MediaDnsName = _settings.ServiceDnsName;
            }

            var mediaPlatformSettings = new MediaPlatformSettings()
            {
                MediaPlatformInstanceSettings = new MediaPlatformInstanceSettings()
                {
                    CertificateThumbprint = _settings.CertificateThumbprint,
                    InstanceInternalPort = _settings.MediaInternalPort,
                    InstancePublicIPAddress = IPAddress.Any,
                    InstancePublicPort = _settings.MediaInstanceExternalPort,
                    ServiceFqdn = _settings.MediaDnsName
                },
                ApplicationId = _settings.AadAppId,
                MediaPlatformLogger = _mediaPlatformLogger
            };

            var notificationUrl = new Uri($"https://{_settings.ServiceDnsName}:{_settings.BotInstanceExternalPort}/{HttpRouteConstants.CallSignalingRoutePrefix}/{HttpRouteConstants.OnNotificationRequestRoute}");

            builder.SetAuthenticationProvider(authProvider);
            builder.SetNotificationUrl(notificationUrl);
            builder.SetMediaPlatformSettings(mediaPlatformSettings);
            builder.SetServiceBaseUrl(new Uri(AppConstants.PlaceCallEndpointUrl));

            this.Client = builder.Build();
            this.Client.Calls().OnIncoming += this.CallsOnIncoming;
            this.Client.Calls().OnUpdated += this.CallsOnUpdated;
        }

        /// <summary>
        /// End a particular call.
        /// </summary>
        /// <param name="callLegId">The call leg id.</param>
        /// <returns>The <see cref="Task" />.</returns>
        public async Task EndCallByCallLegIdAsync(string callLegId)
        {
            try
            {
                await this.GetHandlerOrThrow(callLegId).Call.DeleteAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Manually remove the call from SDK state.
                // This will trigger the ICallCollection.OnUpdated event with the removed resource.
                this.Client.Calls().TryForceRemove(callLegId, out ICall _);
            }
        }

        /// <summary>
        /// Joins the call asynchronously.
        /// </summary>
        /// <param name="joinCallBody">The join call body.</param>
        /// <returns>The <see cref="ICall" /> that was requested to join.</returns>
        public async Task<ICall?> JoinCallAsync(JoinCallBody joinCallBody)
        {
            try
            {
                // A tracking id for logging purposes. Helps identify this call in logs.
                var scenarioId = Guid.NewGuid();

                var (chatInfo, meetingInfo) = JoinInfo.ParseJoinURL(joinCallBody.JoinUrl);

                var tenantId = (meetingInfo as OrganizerMeetingInfo).Organizer.GetPrimaryIdentity().GetTenantId();
                var mediaSession = this.CreateLocalMediaSession();

                var joinParams = new JoinMeetingParameters(chatInfo, meetingInfo, mediaSession)
                {
                    TenantId = tenantId,
                };             

                var call = await this.Client.Calls().AddAsync(joinParams, scenarioId).ConfigureAwait(false);
                call.GraphLogger.Info($"Call creation complete: {call.Id}");
                _logger.LogInformation($"Call creation complete: {call.Id}");

                // TODO Do we add call to handler collection here? or in the added resources?
                //var callHandler = new CallHandler(call, _settings, _logger);
                //this.CallHandlers[call.Id] = callHandler;

                return call;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "error");
                return null;
            }
        }

        public async Task SynthesizeText(string callId, string text)
        {
            try
            {
                if (this.CallHandlers.TryGetValue(callId, out CallHandler? call))
                {
                    await call.BotMediaStream.SynthesizeText(text);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "error");
            }
        }

        /// <summary>
        /// Creates the local media session.
        /// </summary>
        /// <param name="mediaSessionId">The media session identifier.
        /// This should be a unique value for each call.</param>
        /// <returns>The <see cref="ILocalMediaSession" />.</returns>
        private ILocalMediaSession CreateLocalMediaSession(Guid mediaSessionId = default)
        {
            try
            {
                // create media session object, this is needed to establish call connections
                return this.Client.CreateMediaSession(
                    new AudioSocketSettings
                    {
                        StreamDirections = StreamDirection.Sendrecv,
                        // Note! Currently, the only audio format supported when receiving unmixed audio is Pcm16K
                        SupportedAudioFormat = AudioFormat.Pcm16K,
                        ReceiveUnmixedMeetingAudio = false //get the extra buffers for the speakers
                    },
                    new VideoSocketSettings
                    {
                        StreamDirections = StreamDirection.Inactive
                    },
                    mediaSessionId: mediaSessionId);
            }
            catch (Exception e)
            {
                _logger.LogError(e.Message);
                throw;
            }
        }

        /// <summary>
        /// Incoming call handler.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="args">The <see cref="CollectionEventArgs{TResource}" /> instance containing the event data.</param>
        private void CallsOnIncoming(ICallCollection sender, CollectionEventArgs<ICall> args)
        {
            args.AddedResources.ForEach(call =>
            {
                // The context associated with the incoming call.
                IncomingContext incomingContext =
                    call.Resource.IncomingContext;

                // The RP participant.
                string observedParticipantId =
                    incomingContext.ObservedParticipantId;

                // If the observed participant is a delegate.
                IdentitySet onBehalfOfIdentity =
                    incomingContext.OnBehalfOf;

                // If a transfer occured, the transferor.
                IdentitySet transferorIdentity =
                    incomingContext.Transferor;

                string countryCode = null;
                EndpointType? endpointType = null;

                // Note: this should always be true for CR calls.
                if (incomingContext.ObservedParticipantId == incomingContext.SourceParticipantId)
                {
                    // The dynamic location of the RP.
                    countryCode = call.Resource.Source.CountryCode;

                    // The type of endpoint being used.
                    endpointType = call.Resource.Source.EndpointType;
                }

                IMediaSession mediaSession = Guid.TryParse(call.Id, out Guid callId)
                    ? this.CreateLocalMediaSession(callId)
                    : this.CreateLocalMediaSession();

                // Answer call
                call?.AnswerAsync(mediaSession).ForgetAndLogExceptionAsync(
                    call.GraphLogger,
                    $"Answering call {call.Id} with scenario {call.ScenarioId}.");
            });
        }

        /// <summary>
        /// Updated call handler.
        /// </summary>
        /// <param name="sender">The <see cref="ICallCollection" /> sender.</param>
        /// <param name="args">The <see cref="CollectionEventArgs{ICall}" /> instance containing the event data.</param>
        private void CallsOnUpdated(ICallCollection sender, CollectionEventArgs<ICall> args)
        {
            //TODO
            //foreach (var call in args.AddedResources)
            //{
            //    var callHandler = new CallHandler(call, _settings, _logger);
            //    this.CallHandlers[call.Id] = callHandler;
            //}

            foreach (var call in args.RemovedResources)
            {
                if (this.CallHandlers.TryRemove(call.Id, out CallHandler? handler))
                {
                    handler.Dispose();
                }
            }
        }

        /// <summary>
        /// The get handler or throw.
        /// </summary>
        /// <param name="callLegId">The call leg id.</param>
        /// <returns>The <see cref="CallHandler" />.</returns>
        /// <exception cref="ArgumentException">call ({callLegId}) not found</exception>
        private CallHandler GetHandlerOrThrow(string callLegId)
        {
            if (!this.CallHandlers.TryGetValue(callLegId, out CallHandler handler))
            {
                throw new ArgumentException($"call ({callLegId}) not found");
            }

            return handler;
        }
    }
}

