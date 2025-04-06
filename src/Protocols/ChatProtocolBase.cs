using System;
using System.Threading.Tasks;

namespace IPK25_CHAT
{
    public abstract class ChatProtocolBase : IChatProtocol
    {
        public event EventHandler<MessageReceivedEventArgs>? MessageReceived;
        public event EventHandler<ErrorEventArgs>? Error;

        protected void OnMessageReceived(MessageType type, string content, string displayName = null!)
        {
            MessageReceived?.Invoke(this, new MessageReceivedEventArgs(type, content, displayName));
        }

        protected void OnError(string message, Exception ex = null!)
        {
            Error?.Invoke(this, new ErrorEventArgs(message, ex));
        }

        public abstract Task SendAuthAsync(string? username, string? displayName, string? secret);
        public abstract Task SendJoinAsync(string? channel, string? displayName);
        public abstract Task SendMessageAsync(string? displayName, string message);
        
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
    }
} 