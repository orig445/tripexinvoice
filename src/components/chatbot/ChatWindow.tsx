import { useEffect, useRef } from "react";
import { X, RotateCcw, Bot } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ChatMessage } from "./ChatMessage";
import { ChatInput } from "./ChatInput";
import { QuickActions } from "./QuickActions";
import { useChatbot } from "@/hooks/useChatbot";

interface ChatWindowProps {
  onClose: () => void;
}

export function ChatWindow({ onClose }: ChatWindowProps) {
  const { messages, isLoading, config, sendMessage, startNewSession } = useChatbot();
  const scrollRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    scrollRef.current?.scrollTo({ top: scrollRef.current.scrollHeight, behavior: "smooth" });
  }, [messages, isLoading]);

  const handleAction = (action: string, intent: string) => {
    if (action === "camera") {
      // Navigate to upload tab
      window.location.href = "/";
    } else if (action === "redirect") {
      window.location.href = "/";
    }
  };

  const botName = config?.bot_name || "TripEX AI";
  const welcomeMsg = config?.welcome_message || "שלום! איך אוכל לעזור?";

  return (
    <div className="flex flex-col w-[360px] h-[500px] sm:w-[380px] sm:h-[520px] bg-background rounded-2xl shadow-xl border overflow-hidden animate-in slide-in-from-bottom-4 duration-300">
      {/* Header */}
      <div className="flex items-center justify-between px-4 py-3 bg-gradient-to-r from-primary to-triplex-teal-light text-primary-foreground">
        <div className="flex items-center gap-2.5">
          <div className="w-8 h-8 rounded-full bg-primary-foreground/20 flex items-center justify-center">
            <Bot className="h-4 w-4" />
          </div>
          <div>
            <h3 className="text-sm font-semibold">{botName}</h3>
            <p className="text-[10px] opacity-80">מחובר</p>
          </div>
        </div>
        <div className="flex gap-1">
          <Button
            variant="ghost"
            size="icon"
            className="h-7 w-7 text-primary-foreground/80 hover:text-primary-foreground hover:bg-primary-foreground/10"
            onClick={startNewSession}
          >
            <RotateCcw className="h-3.5 w-3.5" />
          </Button>
          <Button
            variant="ghost"
            size="icon"
            className="h-7 w-7 text-primary-foreground/80 hover:text-primary-foreground hover:bg-primary-foreground/10"
            onClick={onClose}
          >
            <X className="h-3.5 w-3.5" />
          </Button>
        </div>
      </div>

      {/* Messages */}
      <div ref={scrollRef} className="flex-1 overflow-y-auto p-3 space-y-3" dir="rtl">
        {/* Welcome message */}
        {messages.length === 0 && (
          <div className="flex gap-2.5">
            <div className="w-7 h-7 rounded-full bg-gradient-to-br from-primary to-triplex-teal-light text-primary-foreground flex items-center justify-center flex-shrink-0 mt-0.5">
              <Bot className="h-3.5 w-3.5" />
            </div>
            <div className="bg-muted rounded-2xl rounded-tl-sm px-3.5 py-2.5 text-sm max-w-[80%]">
              {welcomeMsg}
            </div>
          </div>
        )}

        {messages.map((msg) => (
          <ChatMessage key={msg.id} message={msg} onAction={handleAction} />
        ))}

        {isLoading && (
          <div className="flex gap-2.5">
            <div className="w-7 h-7 rounded-full bg-gradient-to-br from-primary to-triplex-teal-light text-primary-foreground flex items-center justify-center flex-shrink-0">
              <Bot className="h-3.5 w-3.5" />
            </div>
            <div className="bg-muted rounded-2xl rounded-tl-sm px-4 py-3">
              <div className="flex gap-1">
                <span className="w-1.5 h-1.5 bg-muted-foreground/50 rounded-full animate-bounce" style={{ animationDelay: "0ms" }} />
                <span className="w-1.5 h-1.5 bg-muted-foreground/50 rounded-full animate-bounce" style={{ animationDelay: "150ms" }} />
                <span className="w-1.5 h-1.5 bg-muted-foreground/50 rounded-full animate-bounce" style={{ animationDelay: "300ms" }} />
              </div>
            </div>
          </div>
        )}
      </div>

      {/* Quick actions when no messages */}
      {messages.length === 0 && <QuickActions onAction={(text) => sendMessage(text)} />}

      {/* Input */}
      <ChatInput onSend={sendMessage} isLoading={isLoading} />
    </div>
  );
}
