using System;
using System.Threading.Tasks;

namespace IPK25_CHAT
{
    public abstract class ChatProtocolBase : IChatProtocol
    {
        public event EventHandler<MessageReceivedEventArgs>? MessageReceived;
        public event EventHandler<ErrorEventArgs>? Error;
        
        protected bool _Logging = true;
        
        public bool Logging 
        { 
            get { return _Logging; } 
            set { _Logging = value; }
        }

        protected void OnMessageReceived(MessageType type, string content, string displayName = null!)
        {
            LogDebug("Invoking MessageReceived Event");
            MessageReceived?.Invoke(this, new MessageReceivedEventArgs(type, content, displayName));
        }

        protected void OnError(string message, Exception ex = null!)
        {
            Error?.Invoke(this, new ErrorEventArgs(message, ex));
        }

        public abstract Task SendAuthAsync(string? username, string? displayName, string? secret);
        public abstract Task SendJoinAsync(string? channel, string? displayName);
        public abstract Task SendMessageAsync(string? displayName, string message);
        
        public abstract Task SendErrorAsync(string? displayName, string message);
        
        public virtual async Task SendByeAsync(string? displayName)
        {
            try
            {
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                OnError("Failed to send BYE message", ex);
                throw;
            }
        }
        
        public virtual async Task DisconnectAsync()
        {
            try
            {
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                OnError("Error disconnecting", ex);
            }
        }
        
        protected void ProcessMessage(string message)
        {
            try
            {
                if (string.IsNullOrEmpty(message))
                    return;

                LogDebug($"Processing message: '{message}'");
                
                string content = message;
                string? displayName = null;
                MessageType messageType = MessageType.Message;

                if (message.StartsWith("REPLY"))
                {
                    messageType = MessageType.Reply;
                    content = message.Substring(5).Trim();
                    LogDebug($"Parsed as REPLY: '{content}'");
                }
                else if (message.StartsWith("ERR FROM"))
                {
                    messageType = MessageType.Error;
                    displayName = message.Split(' ')[2];
                    content = message.Substring(message.IndexOf("IS", StringComparison.Ordinal) + 2).Trim();
                    LogDebug($"Parsed as ERR: '{content}'");
                }
                else if (message.StartsWith("BYE FROM"))
                {
                    messageType = MessageType.Bye;
                    content = message.Substring(3).Trim();
                    LogDebug($"Parsed as BYE: '{content}'");
                }
                else if (message.StartsWith("MSG FROM"))
                {
                    messageType = MessageType.Message;
                    
                    // Delete "MSG FROM" 
                    string msgBody = message.Substring(9).Trim();
                    
                    // Find " IS " separator
                    int isIndex = msgBody.IndexOf(" IS ", StringComparison.Ordinal);
                    if (isIndex > 0)
                    {
                        // Extract displayName and content
                        displayName = msgBody.Substring(0, isIndex).Trim();
                        content = msgBody.Substring(isIndex + 4).Trim();
                        LogDebug($"Parsed as MSG: displayName='{displayName}', content='{content}'");
                    }
                    else
                    {
                        // If " IS " separator not found, treat as unknown format
                        displayName = "Unknown";
                        content = msgBody;
                        LogDebug($"Parsed as MSG without IS separator: content='{content}'");
                    }
                }
                else
                {
                    OnError($"Unknown message format: '{message}'");
                    return;
                }

                LogDebug($"Forwarding message: Type={messageType}, Content='{content}', DisplayName='{displayName}'");
                OnMessageReceived(messageType, content, displayName!);
            }
            catch (Exception ex)
            {
                LogDebug($"Error processing message '{message}': {ex.Message}");
                OnError($"Error processing message: {message}", ex);
            }
        }
        
        protected void LogDebug(string message)
        {
            if (_Logging)
            {
                Console.Error.WriteLine($"PROTOCOL DEBUG: {message}");
            }
        }
    }
} 