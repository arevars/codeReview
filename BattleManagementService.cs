using Animalia.Backend.API.Actions;
using Animalia.Backend.API.Handlers;
using Animalia.Backend.API.Interfaces;
using Animalia.Backend.API.Models;
using Animalia.Backend.API.Utils;
using Animalia.Backend.Data.Helpers;
using Animalia.Backend.Data.Models.EF;
using Animalia.Backend.Repository.Interfaces;
using Animalia.Shared.Proto;
using AutoMapper;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;

namespace Animalia.Backend.API.Services.gRPC
{
    [Authorize(Roles = "Administrator,User")]
    public class BattleManagementService : BattleManagement.BattleManagementBase
    {
        private readonly IFirstTurnDecisionService _firstTurnDecisionService;
        private readonly IMapper _mapper;
        private readonly ILogger<BattleManagementService> _logger;
        private readonly IGrpcServiceUoW _unitOfWork;
        private readonly ILobbyService _lobby;
        private readonly PlayerManager _playerManager;
        private readonly TimerService _timer;
        private readonly string _gameServer = "GameServer";
        private readonly BattleResultHandler _battleResultHandler;
        private readonly BattleHandler _battleHandler;
        private readonly IFreeTitansService _freeTitansService;

        public BattleManagementService(
            IFirstTurnDecisionService firstTurnDecisionService,
            ILogger<BattleManagementService> logger,
            ILobbyService lobbyService,
            IMapper mapper,
            PlayerManager playerManager,
            TimerService timerService,
            BattleResultHandler battleResultHandler,
            BattleHandler battleHandler,
            IFreeTitansService freeTitansService,
            IGrpcServiceUoW unitOfWork)
        {
            _firstTurnDecisionService = firstTurnDecisionService;
            _logger = logger;
            _mapper = mapper;
            _unitOfWork = unitOfWork;
            _lobby = lobbyService;
            _playerManager = playerManager;
            _timer = timerService;
            _battleResultHandler = battleResultHandler;
            _battleHandler = battleHandler;
            _freeTitansService = freeTitansService;
            _mapper = mapper;
        }

        public override async Task ConnectToFvFBattle(FvFBattleConnectRequest request, IServerStreamWriter<BattleConnectResponse> responseStream, ServerCallContext context)
        {
            try
            {
                var player = new WaitingFvFPlayerModel(request, responseStream);
                _lobby.AddFvFPlayer(player);

                WaitingFvFPlayerModel opponent = _lobby.GetFvFPlayer(request.OpponentId, request.UserId);

                while (!context.CancellationToken.IsCancellationRequested && !player.IsHandled)
                {
                    if (opponent != null)
                    {
                        var battle = await _battleHandler.CreateBattle(request.UserId, opponent.UserId);

                        await _battleHandler.CreateBattleDetailAndUpdateUserStatus(player.UserId, opponent.UserId, battle, BattleType.FvF, BattleType.FvF);

                        StartBattleUserModel playerModel = await CreateStartBattleUserModel(player);
                        StartBattleUserModel opponentModel = await CreateStartBattleUserModel(opponent);

                        await opponent.Stream.WriteAsync(new BattleConnectResponse()
                        {
                            BattleId = battle.Id,
                            Users = { playerModel, opponentModel }
                        });

                        await player.Stream.WriteAsync(new BattleConnectResponse()
                        {
                            BattleId = battle.Id,
                            Users = { playerModel, opponentModel }
                        });

                        opponent.IsHandled = true;

                        _lobby.RemoveFvFPlayer(opponent.UserId, opponent.OpponentId);
                        _lobby.RemoveFvFPlayer(player.UserId, player.OpponentId);

                        break;
                    }
                }
            }
            catch (RpcException ex)
            {
                _logger.LogInformation("------------------ FvF lobby error ------------------\n", ex.Message);
                throw new RpcException(ex.Status, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogInformation("------------------ FvF lobby error ------------------\n", ex.Message);
                throw new RpcException(new Status(StatusCode.Internal, ex.Message));
            }
        }

        public override async Task ConnectToPvPBattle(BattleConnectRequest request, IServerStreamWriter<BattleConnectResponse> responseStream, ServerCallContext context)
        {
            try
            {
                _logger.LogInformation("------------------connect to battle------------\n");

                if (await _playerManager.PlayerInTheLobby(request.UserId) ||
                    await _playerManager.PlayerInTheBattle(request.UserId))
                {
                    throw new RpcException(new Status(StatusCode.Cancelled, $"{request.UserId} is in the lobby or in the battle already"));
                }


                var player = new WaitingPvPPlayerModel()
                {
                    UserId = request.UserId,
                    UserMMR = request.UserMMR,
                    UserName = request.UserName,
                    UserRank = request.UserRank,
                    TitanId = request.TitanId,
                    DeckId = request.DeckId,
                    Stream = responseStream,
                    BattleType = (BattleType)request.BattleType
                };

                await _lobby.SetPvPPlayer(player);

                var opponent = await _playerManager.FindOpponent(player);

                while (!context.CancellationToken.IsCancellationRequested && player.IsWaitingTimerElapsed && !player.IsOpponentFound)
                {
                    if (opponent.UserId is not null)
                    {
                        _logger.LogInformation("------------------found opponent------------\n");
                        _logger.LogInformation("------------------create battle------------\n");

                        var battle = await _battleHandler.CreateBattle(request.UserId, opponent.UserId);

                        await _battleHandler.CreateBattleDetailAndUpdateUserStatus(player.UserId, opponent.UserId, battle, player.BattleType, opponent.BattleType);

                        StartBattleUserModel playerModel = await CreateStartBattleUserModel(player);
                        StartBattleUserModel opponentModel = await CreateStartBattleUserModel(opponent);

                        await player.Stream.WriteAsync(new BattleConnectResponse
                        {
                            BattleId = battle.Id,
                            Users = { playerModel, opponentModel }
                        });

                        await opponent.Stream.WriteAsync(new BattleConnectResponse
                        {
                            BattleId = battle.Id,
                            Users = { playerModel, opponentModel }
                        });

                        await _lobby.DeletePvPPlayer(player);
                        await _lobby.DeletePvPPlayer(opponent);

                        opponent.IsOpponentFound = true;

                        return;
                    }
                }

                if (context.CancellationToken.IsCancellationRequested) await _lobby.DeletePvPPlayer(player);
            }
            catch (RpcException ex)
            {
                _logger.LogInformation("------------------ lobby error ------------\n", ex.Message);
                throw new RpcException(ex.Status, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogInformation("------------------lobby error------------\n", ex.Message);
                throw new RpcException(new Status(StatusCode.Internal, ex.Message));
            }
        }

        public override async Task PlayBattle(
            IAsyncStreamReader<BattleData> requestStream,
            IServerStreamWriter<BattleData> responseStream,
            ServerCallContext context)
        {
            if (!await requestStream.MoveNext())
            {
                _logger.LogInformation($"----------------End of stream------------------");
                DisposeBattleData(requestStream);
                return;
            }

            var actionHandler = new ActionModelBuilderFactory(new InjectedServices(
                _unitOfWork,
                _logger,
                _playerManager,
                _timer,
                _battleResultHandler,
                _battleHandler));

            var battleId = requestStream.Current.BattleId;

            var player = new PlayerModel
            {
                UserId = requestStream.Current.UserId,
                BattleId = battleId,
                Stream = responseStream
            };

            _logger.LogInformation($"PlayBattle {requestStream.Current.UserId}");

            try
            {
                if (_playerManager.CheckIfDisconnected(battleId, player))
                {
                    await HandleReconnect(battleId, responseStream, actionHandler);
                }
                else
                {
                    _playerManager.AddPlayer(battleId, player);
                }

                _logger.LogInformation($"------Starting battle, battleId = {battleId}, player == {player.UserId}\n");

                while (await requestStream.MoveNext())
                {
                    _logger.LogInformation($"Battle, battleId = {battleId} and {requestStream.Current.UserId}\n");

                    if (requestStream.Current.BattleId == battleId) //&& requestStream.Current.UserId == _playerManager.GetPlayerTurn(battleId))
                    {
                        _logger.LogInformation($"Battle, battleId = {battleId} and {requestStream.Current.UserId} \n");

                        await actionHandler.GetActionModelBuilder(requestStream.Current).HandleAction();
                    }
                }
            }
            catch (IOException)
            {
                _logger.LogError($"------------------- User disconnected. UserID -  {requestStream.Current.UserId} ------------------");

                await HandleDisconnect(requestStream, actionHandler);
            }
            catch (Exception E)
            {
                _logger.LogError($"------------------- PlayBattle {E.Message}------------------");
                DisposeBattleData(requestStream);

                throw new RpcException(new Status(StatusCode.Unknown, E.Message));
            }
        }

        [Authorize(Roles = "Administrator")]
        public override async Task<Empty> ResetAllBattlesData(Empty request, ServerCallContext context)
        {
            try
            {
                var battles = await _unitOfWork.BattlesRepository.GetAllAsync();
                foreach (var battle in battles) _unitOfWork.BattlesRepository.Delete(battle);

                var events = await _unitOfWork.BattleEventsRepository.GetAllAsync();
                foreach (var item in events) _unitOfWork.BattleEventsRepository.Delete(item);

                await _unitOfWork.SaveAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }

            return await Task.FromResult(new Empty());
        }

        private async Task<StartBattleUserModel> CreateStartBattleUserModel(WaitingFvFPlayerModel player)
        {
            var deck = await _unitOfWork.DecksRepository.GetFirstOrDefaultAsync(d => d.Id == player.DeckId);

            if (deck == null) throw new ArgumentNullException("Requested deck is not exist");

            deck.ShuffleCards();

            return new StartBattleUserModel()
            {
                UserId = player.UserId,
                UserMMR = player.UserMMR,
                UserName = player.UserName,
                UserRank = player.UserRank,
                Deck = _mapper.Map<DeckModel>(deck),
                Titan = await GetUserTitan(player.TitanId)
            };
        }

        private async Task<StartBattleUserModel> CreateStartBattleUserModel(WaitingPvPPlayerModel player)
        {
            var deck = await _unitOfWork.DecksRepository.GetFirstOrDefaultAsync(d => d.Id == player.DeckId);

            if (deck == null)
            {
                await _lobby.DeletePvPPlayer(player);

                throw new ArgumentNullException("Requested deck is not exist");
            }

            deck.ShuffleCards();

            return new StartBattleUserModel()
            {
                UserId = player.UserId,
                UserMMR = player.UserMMR,
                UserName = player.UserName,
                UserRank = player.UserRank,
                Deck = _mapper.Map<DeckModel>(deck),
                Titan = await GetUserTitan(player.TitanId)
            };
        }

        private void DisposeBattleData(IAsyncStreamReader<BattleData> requestStream)
        {
            try
            {
                _firstTurnDecisionService.RemoveBattleDecision(requestStream.Current.BattleId);

                var players = _playerManager.GetPlayers(requestStream.Current.BattleId);
                if (players.Any())
                {
                    players.ForEach(obj => _timer.Clear(obj.UserId));
                }

                _playerManager.RemovePlayersByBattleId(requestStream.Current.BattleId);
                _playerManager.ClearTurns(requestStream.Current.BattleId);
                _timer.Clear(requestStream.Current.BattleId);

                _logger.LogInformation($"------------------- Battle disposed successfully ------------------");
            }
            catch (Exception E)
            {
                _logger.LogError($"------------------- DisposeBattleData {E.Message}------------------");
            }
        }

        private async Task<TitanDataModel> GetUserTitan(long titanId)
        {
            if (titanId > 0)
            {
                var titanInDb = await _unitOfWork.NFTTitansRepository.GetFirstOrDefaultAsync(titan => titan.Id == (ulong)titanId);

                if (titanInDb != null) return _mapper.Map<TitanDataModel>(titanInDb);
            }
            else
            {
                var freeTitan = await _freeTitansService.GetTitanById(titanId);

                if (freeTitan != null) return _mapper.Map<TitanDataModel>(freeTitan);
            }

            return null;
        }

        private async Task HandleDisconnect(IAsyncStreamReader<BattleData> requestStream, ActionModelBuilderFactory actionHandler)
        {
            //var actionPlayerDisconnected = new BattleData
            //{
            //    UserId = _gameServer,
            //    BattleId = requestStream.Current.BattleId,
            //    PlayerDisconnectedData = new PlayerDisconnected
            //    {
            //        UserId = requestStream.Current.UserId,
            //    }
            //};

            //await actionHandler.GetActionModelBuilder(actionPlayerDisconnected).HandleAction();

            //var actionBattleEnd = new BattleData
            //{
            //    UserId = _gameServer,
            //    BattleId = requestStream.Current.BattleId,
            //    BattleEnd = new BattleEnd
            //    {
            //        WinnerId = _playerManager.GetConnectedPlayers(requestStream.Current.BattleId).First().UserId,
            //        LoserId = requestStream.Current.UserId,
            //    }
            //};

            //await actionHandler.GetActionModelBuilder(actionBattleEnd).HandleAction();
            //_logger.LogWarning($"------------------- PlayBattle ended because of disconnection ------------------");
            _logger.LogWarning($"------------------- HandleDisconnect called ------------------");
            DisposeBattleData(requestStream);
        }

        private async Task HandleReconnect(string battleId, IServerStreamWriter<BattleData> responseStream, ActionModelBuilderFactory actionHandler)
        {
            var disconnectedPlayer = _playerManager.GetDisconnectedPlayer(battleId);

            disconnectedPlayer.Status = PlayerInBattleStatus.Connected;
            disconnectedPlayer.Stream = responseStream;

            var actionGetBattleStateModel = new BattleData
            {
                UserId = _gameServer,
                BattleId = battleId,
                GetBattleStateModelData = _battleHandler.GetBattleState(battleId)
            };
            await actionHandler.GetActionModelBuilder(actionGetBattleStateModel).HandleAction();

            var actionPlayerReconnected = new BattleData
            {
                UserId = _gameServer,
                BattleId = battleId,
                PlayerReconnectedData = new PlayerReconnected
                {
                    UserId = _playerManager.GetDisconnectedPlayer(battleId).UserId,
                }
            };

            await actionHandler.GetActionModelBuilder(actionPlayerReconnected).HandleAction();
        }

        private async Task UpdateUserStatus(string userId)
        {
            UserData userData = await _unitOfWork.UserDataRepository.GetFirstOrDefaultAsync(u => u.UserId == userId);

            if (userData != null)
            {
                userData.UserStatus = (int)UserCurrentStatus.Online;
                _unitOfWork.UserDataRepository.Update(userData);
                await _unitOfWork.SaveAsync();
            }
        }
    }
}
