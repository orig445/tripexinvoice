import { Bot, User, Camera, ArrowRight, HelpCircle } from "lucide-react";
import { Button } from "@/components/ui/button";
import type { ChatMessage as ChatMessageType } from "@/hooks/useChatbot";

interface ChatMessageProps {
  message: ChatMessageType;
  onAction?: (action: string, intent: string) => void;
}

export function ChatMessage({ message, onAction }: ChatMessageProps) {
  const isUser = message.role === "user";
  const action = message.metadata?.action;
  const intent = message.intent;

  return (
    <div className={`flex gap-2.5 ${isUser ? "flex-row-reverse" : "flex-row"}`}>
      <div
        className={`w-7 h-7 rounded-full flex items-center justify-center flex-shrink-0 mt-0.5 ${
          isUser
            ? "bg-primary/10 text-primary"
            : "bg-gradient-to-br from-primary to-triplex-teal-light text-primary-foreground"
        }`}
      >
        {isUser ? <User className="h-3.5 w-3.5" /> : <Bot className="h-3.5 w-3.5" />}
      </div>

      <div className={`max-w-[80%] space-y-1.5`}>
        <div
          className={`rounded-2xl px-3.5 py-2.5 text-sm leading-relaxed ${
            isUser
              ? "bg-primary text-primary-foreground rounded-tr-sm"
              : "bg-muted text-foreground rounded-tl-sm"
          }`}
        >
          {message.content}
        </div>

        {/* Action buttons */}
        {!isUser && action && action !== "none" && onAction && (
          <div className="flex gap-1.5">
            {action === "camera" && (
              <Button
                size="sm"
                variant="outline"
                className="h-7 text-xs gap-1"
                onClick={() => onAction(action, intent || "scan")}
              >
                <Camera className="h-3 w-3" />
                פתח סורק
              </Button>
            )}
            {action === "redirect" && (
              <Button
                size="sm"
                variant="outline"
                className="h-7 text-xs gap-1"
                onClick={() => onAction(action, intent || "general")}
              >
                <ArrowRight className="h-3 w-3" />
                {intent === "expense" ? "נהל הוצאות" : intent === "online" ? "הזמנה" : "המשך"}
              </Button>
            )}
          </div>
        )}
      </div>
    </div>
  );
}
