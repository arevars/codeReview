using AutoMapper;
using Grpc.Core;
using MetaCasinoAPI.Interfaces;
using MetaCasinoAPI.Models.Common;
using MetaCasinoAPI.Models.Configs;
using MetaCasinoAPI.Models.DataArtAPI.Roulette;
using MetaCasinoAPI.Protos.Roulette;
using MetaCasinoAPI.Services.gRPC.Roulette.StompMessageHandlers;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using StompMessageType = MetaCasinoAPI.Models.DataArtAPI.Roulette.Enums.StompMessageType;

namespace MetaCasinoAPI.Services.gRPC.Roulette;

public class RouletteGameService : RouletteGame.RouletteGameBase
{
    private readonly ILogger<RouletteGameService> _logger;
    private readonly IDataArtAPIRouletteService _dataArtAPIRouletteService;
    private readonly IMapper _mapper;
    private readonly RouletteConfig _config;
    private readonly IStompService _stomp;
    private ConcurrentQueue<RouletteStreamResponse> _responseMessageQueue = new();
    private ConcurrentQueue<RouletteStreamRequest> _requestMessageQueue = new();
    private readonly Dictionary<StompMessageType, IRouletteStompMessageHandler> _handlers;
    private string _userId = string.Empty;
    private readonly JsonSerializerOptions _stompSerializerSettings = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public RouletteGameService(
        ILogger<RouletteGameService> logger,
        IDataArtAPIRouletteService dataArtAPIService,
        IMapper mapper,
        IOptions<RouletteConfig> config,
        IStompService stomp,
        Dictionary<StompMessageType, IRouletteStompMessageHandler> handlers
        )
    {
        _logger = logger;
        _dataArtAPIRouletteService = dataArtAPIService;
        _mapper = mapper;
        _config = config.Value;
        _stomp = stomp;
        _handlers = handlers;
        _stomp.SetJsonSerializerOptions(_stompSerializerSettings);
    }

    public override async Task ConnectAndPlay(
        IAsyncStreamReader<RouletteStreamRequest> requestStream,
        IServerStreamWriter<RouletteStreamResponse> responseStream,
        ServerCallContext context)
    {
        try
        {
            var processingTask = Task.Run(async () =>
            {
                try
                {
                    while (!context.CancellationToken.IsCancellationRequested)
                    {
                        if (_responseMessageQueue.TryDequeue(out var messageFromStomp))
                        {
                            await responseStream.WriteAsync(messageFromStomp);
                        }
                        else
                        {
                            await Task.Delay(10); // Wait for new messages to arrive
                        }
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError($"-------------- Roulette. ConnectAndPlay Response processing Error: {e.Message}");
                }
            });

            while (await requestStream.MoveNext())
            {
                _requestMessageQueue.Enqueue(requestStream.Current);
                var response = _requestMessageQueue.TryDequeue(out var result);
                if (response && result is not null)
                {
                    await HandleRequest(result);
                }
            }

            context.CancellationToken.Register(() => _responseMessageQueue.Clear());
            await processingTask;
        }
        catch (Exception e)
        {
            _logger.LogError($"-------------- Roulette. ConnectAndPlay disconnect initiated from client side. UserId: {_userId} #### Main handling Error: {e.Message}");
            _stomp.HandleDisconnect();
        }
    }


    private RouletteStreamResponse HandleResponse(string jsonString)
    {
        var response = new RouletteStreamResponse();

        try
        {
            var message = _stomp.ParseMessage(jsonString);
            var messageTypeProperty = message.GetProperty("messageType").GetString();
            bool isMessageTypeValid = !string.IsNullOrEmpty(messageTypeProperty);

            if (isMessageTypeValid && Enum.TryParse<StompMessageType>(messageTypeProperty, out var messageTypeEnum))
            {
                if (_handlers.TryGetValue(messageTypeEnum, out var handler))
                {
                    response = handler.HandleMessage(message);
                }
                else
                {
                    _logger.LogError($"-------------- Roulette. No handler found for message type: {messageTypeProperty}");
                }
            }
            else
            {
                _logger.LogError($"-------------- Roulette. Unknown message type {messageTypeProperty}");
            }
        }
        catch (Exception e)
        {
            _logger.LogError($"-------------- Roulette. Error HandleResponse: {e.Message}");
        }

        _responseMessageQueue.Enqueue(response);
        return response;
    }

    private async Task HandleRequest(RouletteStreamRequest request) // @TODO refactor this method
    {
        
    }

    public override async Task<RouletteSitDownResponse> SitDown(RouletteSitDownRequest request, ServerCallContext context)
    {
        var response = new RouletteSitDownResponse();
        try
        {
            var mappedModel = _mapper.Map<DataArtAPIRouletteSitDownRequest>(request);
            var result = await _dataArtAPIRouletteService.SitDown(mappedModel);
            response = _mapper.Map<RouletteSitDownResponse>(result);
        }
        catch (Exception e)
        {
            _logger.LogError($"RouletteGameService Error ### SitDown ### {e.Message}");
            response.Status = (int)HttpStatusCode.InternalServerError;
            response.Message = e.Message;
        }

        return await Task.FromResult(response);
    }

    public override async Task<RouletteGameTablesResponse> GetGameTables(RouletteGameTablesRequest request, ServerCallContext context)
    {
        try
        {
            var mappedModel = _mapper.Map<DataArtAPIRouletteGameTablesRequest>(request);
            foreach (var tableId in request.TableIds)
            {
                mappedModel.LobbyTableListReq.Add(new LobbyTableId()
                {
                    TableId = tableId
                });
            }

            mappedModel.Device = request.DeviceId;

            var response = await _dataArtAPIRouletteService.GetGameTables(mappedModel);
            var responseModel = _mapper.Map<RouletteGameTablesResponse>(response);

            return responseModel;
        }
        catch (Exception e)
        {
            _logger.LogError($"RouletteGameService Error ### GetGameTables ### {e.Message}");
        }

        return await Task.FromResult(new RouletteGameTablesResponse
        {
            Status = (int)HttpStatusCode.InternalServerError,
            Message = "Roulette GetGameTables: Something is wrong"
        });
    }
    
    public async override Task<GetHistoryResponse> GetHistory(GetHistoryRequest request, ServerCallContext context)
    {
        try
        {
            var mappedRequest = _mapper.Map<DataArtAPIHistoryRequest>(request);
            var result = await _dataArtAPIRouletteService.GetHistory(mappedRequest);
            var responseModel = _mapper.Map<GetHistoryResponse>(result);

            return responseModel;
        }
        catch (Exception e)
        {
            _logger.LogError($"RouletteGameService Error ### GetHistory ### {e.Message}");
        }

        return await Task.FromResult(new GetHistoryResponse()
        {
            Status = (int)HttpStatusCode.InternalServerError,
            Message = "GetHistoryResponse: Error getting history"
        });
    }
    public async override Task<GetFavouriteBetsResponse> GetFavouriteBets(GetFavouriteBetsRequest request, ServerCallContext context)
    {
        try
        {
            var mappedRequest = _mapper.Map<DataArtAPIGetFavouriteBetsRequest>(request);
            var result = await _dataArtAPIRouletteService.GetFavouriteBets(mappedRequest);
            var responseModel = _mapper.Map<GetFavouriteBetsResponse>(result);

            return responseModel;
        }
        catch (Exception e)
        {
            _logger.LogError($"RouletteGameService Error ### GetFavouriteBets ### {e.Message}");
        }

        return await Task.FromResult(new GetFavouriteBetsResponse()
        {
            Status = (int)HttpStatusCode.InternalServerError,
            Message = "GetFavouriteBetsResponse: Error getting Favourite bets"
        });
    }

    public async override Task<SetFavouriteBetsResponse> SetFavouriteBets(SetFavouriteBetsRequest request, ServerCallContext context)
    {
        try
        {
            var mappedRequest = _mapper.Map<DataArtAPISetFavouriteBetsRequest>(request);
            var result = await _dataArtAPIRouletteService.SetFavouriteBets(mappedRequest);
            var responseModel = _mapper.Map<SetFavouriteBetsResponse>(result);

            return responseModel;
        }
        catch (Exception e)
        {
            _logger.LogError($"RouletteGameService Error ### SetFavouriteBets ### {e.Message}");
        }

        return await Task.FromResult(new SetFavouriteBetsResponse()
        {
            Status = (int)HttpStatusCode.InternalServerError,
            Message = "GetFavouriteBetsResponse: Error Setting Favourite bets"
        });
    }
}
