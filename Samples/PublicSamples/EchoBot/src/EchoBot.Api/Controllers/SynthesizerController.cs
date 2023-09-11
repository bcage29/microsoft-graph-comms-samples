using System.Net;
using EchoBot.Api.Bot;
using EchoBot.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

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
        public IActionResult SendAsync([FromBody] string callId, string text)
        {
            try
            {
                _logger.LogInformation($"Synthesizing text: ${text}");
                _botService.SynthesizeText(callId, text);
                return Ok();
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Error occurred synthesizing text: ${text}. Method: {this.Request.Method}, {this.Request.Path}");

                return Problem(detail: e.StackTrace, statusCode: (int)HttpStatusCode.InternalServerError, title: e.Message);
            }
        }
    }
}
