using Azure.Messaging.ServiceBus;
using Newtonsoft.Json;
using SengokuProvider.Library.Models.Events;
using SengokuProvider.Library.Services.Common;
using SengokuProvider.Worker.Factories;

namespace SengokuProvider.Worker.Handlers
{
    public class EventReceivedWorker : BackgroundService
    {
        private readonly ILogger<EventReceivedWorker> _log;
        private readonly IEventHandlerFactory _eventFactory;
        private readonly IConfiguration _configuration;
        private readonly CommandProcessor _commandProcessor;

        private ServiceBusClient _client;
        private ServiceBusProcessor? _processor;

        public EventReceivedWorker(ILogger<EventReceivedWorker> logger, IConfiguration config, CommandProcessor commandProcessor, ServiceBusClient client, IEventHandlerFactory eventFactory)
        {
            _log = logger;
            _configuration = config;
            _commandProcessor = commandProcessor;
            _client = client;
            _eventFactory = eventFactory;
        }
        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (_log.IsEnabled(LogLevel.Information))
                {
                    _log.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
                }
                _processor = _client.CreateProcessor(_configuration["ReceivedQueue"], new ServiceBusProcessorOptions { MaxConcurrentCalls = 5, PrefetchCount = 5, });
                _processor.ProcessMessageAsync += MessageHandler;
                _processor.ProcessErrorAsync += Errorhandler;
            }

            return Task.CompletedTask;
        }
        private Task Errorhandler(ProcessErrorEventArgs args)
        {
            _log.LogError($"Error Processing Message: {args.ErrorSource}: {args.FullyQualifiedNamespace} {args.EntityPath} {args.Exception}");
            return Task.CompletedTask;
        }
        private async Task MessageHandler(ProcessMessageEventArgs args)
        {
            _log.LogWarning("Received Message...");

            var currentMessage = await ParseMessage(args.Message);
            if (currentMessage == null) { return; }

            try
            {
                switch (currentMessage.Topic)
                {
                    case EventCommandRegistry.UpdateEvent:
                        await UpdateEvent(currentMessage);
                        break;
                    case EventCommandRegistry.IntakeEventsByLocation:
                        await IntakeLocationEvents(currentMessage);
                        break;
                }
                await args.CompleteMessageAsync(args.Message);
            }
            catch (Exception ex)
            {
                _log.LogError(ex.Message, ex);
                await args.DeadLetterMessageAsync(args.Message);
                throw;
            }
        }
        private async Task IntakeLocationEvents(EventReceivedData? currentMessage)
        {
            if (currentMessage == null) { return; }
            if (currentMessage.Command is IntakeEventsByLocationCommand intakeCommand)
            {
                var currentIntakeHandler = _eventFactory.CreateIntakeHandler();
                var result = await currentIntakeHandler.IntakeTournamentData(intakeCommand);
                if (result.Count <= 0) new ApplicationException($"Failed to Intake Tournament Batch");
            }
            else { throw new InvalidOperationException("Command is not of expected type IntakeLocationCommand"); }
        }
        private async Task UpdateEvent(EventReceivedData? currentMessage)
        {
            if (currentMessage == null) { return; }
            if (currentMessage.Command is UpdateEventCommand updateCommand)
            {
                var currentUpdateHandler = _eventFactory.CreateIntakeHandler();
                var result = await currentUpdateHandler.UpdateEventData(updateCommand);
                if (!result) new ApplicationException($"Failed to update Event Data");
            }
            else { throw new InvalidOperationException("Command is not of expected type UpdateEventCommand"); }
            _log.LogInformation("Successfully Updated Event");
        }
        private async Task<EventReceivedData?> ParseMessage(ServiceBusReceivedMessage message)
        {
            using Stream bodyStream = message.Body.ToStream();
            using var reader = new StreamReader(bodyStream);

            var data = await reader.ReadToEndAsync();

            try
            {
                return JsonConvert.DeserializeObject<EventReceivedData>(data);
            }
            catch (JsonException ex)
            {
                _log.LogError(ex.Message);
            }
            return null;
        }
    }
}
