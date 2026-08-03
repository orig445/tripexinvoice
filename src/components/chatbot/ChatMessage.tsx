import { useState } from "react";
import { User, Camera, ArrowRight, PlusCircle, BarChart3, ThumbsUp, GraduationCap } from "lucide-react";
import { Button } from "@/components/ui/button";
import { toast } from "sonner";
import type { ChatMessage as ChatMessageType } from "@/hooks/useChatbot";
import { TeachMiloDialog } from "./TeachMiloDialog";
import myloWaving from "@/assets/mylo-waving.jpeg";
import myloReading from "@/assets/mylo-reading.jpeg";
import myloDetective from "@/assets/mylo-detective.jpeg";
import myloThinking from "@/assets/mylo-thinking.jpeg";

interface ChatMessageProps {
  message: ChatMessageType;
  onAction?: (action: string, data?: Record<string, any>) => void;
  /** The user question this assistant message replies to — used when teaching Milo */
  question?: string;
  audience?: "external" | "internal";
}


function getMiloAvatar(intent?: string): string {
  switch (intent) {
    case "help":
      return myloReading;
    case "scan":
    case "bi":
      return myloDetective;
    case "expense":
    case "online":
      return myloThinking;
    default:
      return myloWaving;
  }
}

export function ChatMessage({ message, onAction }: ChatMessageProps) {
  const isUser = message.role === "user";
  const actions = message.metadata?.actions || [];
  const redirectPage = message.metadata?.redirectPage || "";

  const actionButtons: Record<string, { icon: typeof Camera; label: string }> = {
    Camera:         { icon: Camera,      label: "📷 Scan Invoice" },
    Redirect:       { icon: ArrowRight,  label: redirectPage === "help" ? "Help" : redirectPage === "booking" ? "Booking" : "Continue" },
    AddExpense:     { icon: PlusCircle,  label: "Add Expense" },
    DisplayResults: { icon: BarChart3,   label: "Show Results" },
  };

  const miloAvatar = getMiloAvatar(message.intent);

  return (
    <div className={`flex gap-2.5 ${isUser ? "flex-row-reverse" : "flex-row"}`}>
      {isUser ? (
        <div className="w-7 h-7 rounded-full flex items-center justify-center flex-shrink-0 mt-0.5 bg-primary/10 text-primary">
          <User className="h-3.5 w-3.5" />
        </div>
      ) : (
        <div className="w-8 h-8 rounded-full flex-shrink-0 mt-0.5 overflow-hidden border border-primary/20 animate-in zoom-in duration-300 hover:scale-110 transition-transform">
          <img src={miloAvatar} alt="Milo" className="w-full h-full object-cover" />
        </div>
      )}

      <div className={`max-w-[80%] space-y-1.5`}>
        <div
          className={`rounded-2xl px-3.5 py-2.5 text-sm leading-relaxed ${
            isUser
              ? "bg-primary text-primary-foreground rounded-tr-sm"
              : "bg-muted text-foreground rounded-tl-sm"
          }`}
        >
          {message.content.split("\n").map((line, i) => (
            <span key={i}>
              {line}
              {i < message.content.split("\n").length - 1 && <br />}
            </span>
          ))}
        </div>

        {!isUser && actions.length > 0 && onAction && (
          <div className="flex gap-1.5 flex-wrap">
            {actions.map((action) => {
              const btn = actionButtons[action];
              if (!btn) return null;
              const Icon = btn.icon;
              return (
                <Button
                  key={action}
                  size="sm"
                  variant="outline"
                  className="h-7 text-xs gap-1"
                  onClick={() => onAction(action, { redirectPage })}
                >
                  <Icon className="h-3 w-3" />
                  {btn.label}
                </Button>
              );
            })}
          </div>
        )}
      </div>
    </div>
  );
}
