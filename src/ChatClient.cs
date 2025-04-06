using System;
using System.Threading.Tasks;
using System.Threading;

namespace IPK25_CHAT
{
    public class ChatClient
    {
        private readonly IChatProtocol _protocol;
        private string? _displayName;
        private bool _isAuthenticated;
        private string? _currentChannel;
        private ClientState _currentState = ClientState.Init;
        private string? _requestedChannel; 
        private CancellationTokenSource _cancellationTokenSource;
        private bool _isTerminating = false;

        public ChatClient(IChatProtocol protocol)
        {
            _protocol = protocol;
            _protocol.MessageReceived += HandleMessageReceived;
            _protocol.Error += HandleError;
            _cancellationTokenSource = new CancellationTokenSource();
        }

        public async Task RunAsync()
        {
            try
            {
                Console.CancelKeyPress += HandleCancelKeyPress;
                
                while (!_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    var input = Console.ReadLine();
                    
                    if (input == null)
                    {
                        Console.WriteLine("End of input detected, terminating gracefully...");
                        await GracefulShutdownAsync();
                        break;
                    }
                    
                    if (string.IsNullOrEmpty(input))
                        continue;

                    if (input.StartsWith("/"))
                    {
                        await HandleCommandAsync(input);
                    }
                    else
                    {
                        await HandleMessageAsync(input);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Client operation was cancelled");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Fatal error: {ex}");
                
                await GracefulShutdownAsync();
                throw;
            }
            finally
            {
                Console.CancelKeyPress -= HandleCancelKeyPress;
            }
        }

        private void HandleCancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            
            Task.Run(async () => 
            {
                try
                {
                    Console.WriteLine("Interrupt signal received (Ctrl+C), terminating gracefully...");
                    
                    using var timeoutCts = new CancellationTokenSource(5000);
                    
                    var shutdownTask = GracefulShutdownAsync();
                    
                    if (await Task.WhenAny(shutdownTask, Task.Delay(4000, timeoutCts.Token)) != shutdownTask)
                    {
                        Console.WriteLine("Shutdown taking too long, forcing exit...");
                        Environment.Exit(1);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error during shutdown: {ex.Message}");
                    Environment.Exit(1);
                }
            });
        }

        private async Task GracefulShutdownAsync()
        {
            if (_isTerminating)
                return;
            
            _isTerminating = true;
            
            try
            {
                _cancellationTokenSource?.Cancel();
                
                using var timeoutCts = new CancellationTokenSource(3000);
                
                try
                {
                    if (_protocol != null && _isAuthenticated)
                    {
                        Console.WriteLine("Sending BYE message to server...");
                        
                        var byeTask = _protocol.SendByeAsync(_displayName);
                        await Task.WhenAny(byeTask, Task.Delay(1000, timeoutCts.Token));
                        
                        if (!byeTask.IsCompleted)
                        {
                            Console.WriteLine("BYE message send timed out");
                        }
                        
                        if (_protocol is UdpProtocol && !timeoutCts.IsCancellationRequested)
                        {
                            Console.WriteLine("Waiting for server to acknowledge BYE message...");
                            await Task.Delay(500, timeoutCts.Token);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("BYE operation was cancelled due to timeout");
                }
                
                try
                {
                    if (_protocol != null)
                    {
                        var disconnectTask = _protocol.DisconnectAsync();
                        await Task.WhenAny(disconnectTask, Task.Delay(1000, timeoutCts.Token));
                        
                        if (!disconnectTask.IsCompleted)
                        {
                            Console.WriteLine("Disconnect operation timed out");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("Disconnect operation was cancelled due to timeout");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error during shutdown: {ex.Message}");
            }
            finally
            {
                Console.WriteLine("Shutdown complete");
                Environment.Exit(0);
            }
        }

        private async Task HandleCommandAsync(string command)
        {
            string?[] parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return;

            switch (parts[0]!.ToLower())
            {
                case "/auth":
                    if (_currentState == ClientState.Init || _currentState == ClientState.Auth)
                    {
                        if (parts.Length != 4)
                        {
                            Console.WriteLine("Usage: /auth <username> <secret> <displayname>");
                            return;
                        }

                        await HandleAuthAsync(parts[1], parts[2], parts[3]);
                    }
                    break;

                case "/join":
                    if (_currentState == ClientState.Open)
                    {
                        if (parts.Length != 2)
                        {
                            Console.WriteLine("Usage: /join <channel>");
                            return;
                        }

                        string? requestedChannel = parts[1];

                        _requestedChannel = requestedChannel;

                        await _protocol.SendJoinAsync(requestedChannel, _displayName);

                        Console.WriteLine(
                            $"Join request sent for channel '{requestedChannel}'. Waiting for server confirmation...");
                    }
                    break;

                case "/rename":
                    if (parts.Length != 2)
                    {
                        Console.WriteLine("Usage: /rename <displayname>");
                        return;
                    }
                    _displayName = parts[1];
                    Console.WriteLine($"Display name changed to {_displayName}");
                    break;

                case "/help":
                    PrintHelp();
                    break;
                    
                default:
                    Console.WriteLine("Unknown command. Use /help for available commands");
                    break;
            }
        }

        private async Task HandleAuthAsync(string? username, string? secret, string? displayName)
        {
            try
            {
                await _protocol.SendAuthAsync(username, displayName, secret);
                
                _displayName = displayName;
                
                Console.WriteLine($"Authentication request sent as {displayName}. Waiting for server response...");
                
                if (_currentState == ClientState.Init)
                {
                    _currentState = ClientState.Auth;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Authentication Failure: {ex.Message}");
            }
        }

        private async Task HandleMessageAsync(string message)
        {
            if (!_isAuthenticated)
            {
                Console.WriteLine("You must authenticate first");
                return;
            }

            if (string.IsNullOrEmpty(_currentChannel))
            {
                _currentChannel = "default";
                Console.WriteLine("You are in the default channel");
            }

            Console.WriteLine($"Sending message to channel {_currentChannel}");
            await _protocol.SendMessageAsync(_displayName, message);
        }

        private void HandleMessageReceived(object sender, MessageReceivedEventArgs e)
        {
            Console.WriteLine($"DEBUG: Получено сообщение типа {e.Type}, содержимое: '{e.Content}', отправитель: '{e.DisplayName}'");
            
            try
            {
                switch (e.Type)
                {
                    case MessageType.Reply:
                        string[] replyParts = e.Content.Split(" ");
                        if (replyParts[0] == "OK")
                        {
                            Console.WriteLine($"Action Success: {e.Content.Split(new[] { " IS " }, StringSplitOptions.None)[1]}");
                            if (_currentState == ClientState.Auth)
                            {
                                _isAuthenticated = true;
                                _currentChannel = "default";
                            }
                            else
                            {
                                _currentChannel = _requestedChannel;
                                _requestedChannel = null;

                            }
                            _currentState = ClientState.Open;
                        }
                        else {
                            Console.WriteLine("DEBUG: Negative reply from server");                            Console.WriteLine($"Action Failure: {e.Content.Split(new[] { " IS " }, StringSplitOptions.None)[1]}");
                            if (_currentState == ClientState.Join) _currentState = ClientState.Open;
                            _requestedChannel = null;
                        }
                        break;
                    
                    case MessageType.Message:
                        if (_currentState == ClientState.Open || _currentState == ClientState.Join)
                            Console.WriteLine($"{e.DisplayName}: {e.Content}");
                        break;
                    
                    case MessageType.Error:
                        Console.WriteLine("Received ERR message from server");
                        _currentState = ClientState.End;
                        Environment.Exit(0);
                        break;   
                    
                    case MessageType.Bye:
                        Console.WriteLine("Received BYE message from server");
                        _currentState = ClientState.End;
                        Environment.Exit(0);
                        break;    
                    
                    default:
                        Console.WriteLine($"Unknown message type: {e.Type} - {e.Content}");
                        break;
                }
                
                Console.WriteLine($"DEBUG: Текущее состояние - аутентифицирован: {_isAuthenticated}, текущий канал: {_currentChannel}, отображаемое имя: {_displayName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"DEBUG: Error occured during message parsing: {ex.Message}");
            }
        }
        

        private void HandleError(object sender, ErrorEventArgs e)
        {
            Console.Error.WriteLine($"Error: {e.Message}");
            if (e.Exception != null)
            {
                Console.Error.WriteLine($"Exception: {e.Exception}");
            }
        }

        private void PrintHelp()
        {
            Console.WriteLine("Available commands:");
            Console.WriteLine("  /auth <username> <secret> <displayname> - Authenticate with server");
            Console.WriteLine("  /join <channel> - Join a channel");
            Console.WriteLine("  /rename <displayname> - Change your display name");
            Console.WriteLine("  /help - Show this help message");
            Console.WriteLine("Press Ctrl+C or Ctrl+D to gracefully terminate the client");
        }
    }
    
    public enum ClientState
    {
        Init,
        Auth,
        Open,
        Join,
        End
    }
} 