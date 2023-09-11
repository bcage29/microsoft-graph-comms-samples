using EchoBot.Api.Bot;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Net;

namespace EchoBot.Api.Controllers
{

    [Route("[controller]")]
    [ApiController]
    public class SynthesizerController : ControllerBase
    {
        private readonly ILogger<SynthesizerController> _logger;
        private readonly IBotService _botService;

        public SynthesizerController(ILogger<SynthesizerController> logger,
            IBotService botService)
        {
            _logger = logger;
            _botService = botService;
        }

        /// <summary>
        /// Send text to be synthesized into the meeting
        /// </summary>
        /// <returns></returns>
        [HttpPost]
        public IActionResult SendAsync([FromBody] SynthesizerRequest req)
        {
            try
            {
                _logger.LogInformation($"Synthesizing text for meeting: ${req.MeetingId}");
                _botService.SynthesizeText(req);
                return Ok();
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Error occurred synthesizing text: ${req.Message}. Method: {this.Request.Method}, {this.Request.Path}");

                return Problem(detail: e.StackTrace, statusCode: (int)HttpStatusCode.InternalServerError, title: e.Message);
            }
        }
    }

    public class SynthesizerRequest
    {
        public string MeetingId { get; set; }

        public string Message { get; set; }

        public SynthesizerVoiceInfo VoiceInfo {  get; set; }
    }

    public class SynthesizerVoiceInfo
    {
        public string? Name { get; set; }

        public string? Role { get; set; }
        //Girl,Boy,YoungAdultFemale,YoungAdultMale,OlderAdultFemale,OlderAdultMale,SeniorFemale,SeniorMale

        public string? Style { get; set; }

        public int? StyleDegree { get; set; }
    }
}