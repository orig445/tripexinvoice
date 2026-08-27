import { useState } from "react";
import { User, Camera, ArrowRight, PlusCircle, BarChart3, ThumbsUp, GraduationCap, FileText, ExternalLink, Languages, Loader2 } from "lucide-react";
import { supabase } from "@/integrations/supabase/client";
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

export function ChatMessage({ message, onAction, question, audience = "external" }: ChatMessageProps) {
  const isUser = message.role === "user";
  const actions = message.metadata?.actions || [];
  const sources = message.metadata?.sources || message.metadata?.data?.sources || [];
  const redirectPage = message.metadata?.redirectPage || "";
  const [teachOpen, setTeachOpen] = useState(false);
  const [voted, setVoted] = useState(false);
  const [translation, setTranslation] = useState<string | null>(null);
  const [showTranslation, setShowTranslation] = useState(false);
  const [translating, setTranslating] = useState(false);

  const handleTranslate = async () => {
    if (translation) {
      setShowTranslation((v) => !v);
      return;
    }
    setTranslating(true);
    const { data, error } = await supabase.functions.invoke("translate-text", {
      body: { text: message.content, target: "he" },
    });
    setTranslating(false);
    if (error || !data?.translation) {
      console.error("Translate error:", error);
      toast.error("Translation failed");
      return;
    }
    setTranslation(data.translation);
    setShowTranslation(true);
  };


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

        {showTranslation && translation && (
          <div
            dir="rtl"
            className="rounded-2xl border border-primary/20 bg-background px-3.5 py-2.5 text-sm leading-relaxed text-foreground"
          >
            <p className="mb-1 text-[11px] font-medium text-muted-foreground">Hebrew translation</p>
            {translation.split("\n").map((line, i) => (
              <span key={i}>
                {line}
                {i < translation.split("\n").length - 1 && <br />}
              </span>
            ))}
          </div>
        )}

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

        {!isUser && sources.length > 0 && (
          <div className="space-y-1.5 pt-1" aria-label="Source documents">
            <p className="text-[11px] font-medium text-muted-foreground">Source documents</p>
            <div className="flex flex-wrap gap-1.5">
              {sources.map((source, index) =>
                source.url ? (
                  <a
                    key={`${source.name}-${index}`}
                    href={source.url}
                    target="_blank"
                    rel="noreferrer"
                    className="inline-flex max-w-full items-center gap-1 rounded-md border bg-background px-2 py-1 text-xs text-foreground hover:border-primary hover:text-primary"
                    title={`Open ${source.name}`}
                  >
                    <FileText className="h-3 w-3 shrink-0" />
                    <span className="max-w-56 truncate">{source.name}</span>
                    <ExternalLink className="h-3 w-3 shrink-0" />
                  </a>
                ) : (
                  <span key={`${source.name}-${index}`} className="inline-flex max-w-full items-center gap-1 rounded-md border bg-background px-2 py-1 text-xs text-foreground">
                    <FileText className="h-3 w-3 shrink-0" />
                    <span className="max-w-56 truncate">{source.name}</span>
                  </span>
                ),
              )}
            </div>
          </div>
        )}

        {!isUser && (
          <div className="flex items-center gap-1 pt-0.5">
            <Button
              size="sm"
              variant="ghost"
              className={`h-6 px-1.5 text-[11px] gap-1 text-muted-foreground hover:text-primary ${voted ? "text-primary" : ""}`}
              onClick={() => {
                setVoted(true);
                toast.success("Thanks for the feedback 🦊");
              }}
              title="Good answer"
            >
              <ThumbsUp className="h-3 w-3" />
            </Button>
            <Button
              size="sm"
              variant="ghost"
              className="h-6 px-1.5 text-[11px] gap-1 text-muted-foreground hover:text-primary"
              onClick={() => setTeachOpen(true)}
              title="Teach Milo the right answer"
            >
              <GraduationCap className="h-3 w-3" />
              Teach Milo
            </Button>
            <Button
              size="sm"
              variant="ghost"
              className="h-6 px-1.5 text-[11px] gap-1 text-muted-foreground hover:text-primary"
              onClick={handleTranslate}
              disabled={translating}
              title="Translate to Hebrew"
            >
              {translating ? <Loader2 className="h-3 w-3 animate-spin" /> : <Languages className="h-3 w-3" />}
              {showTranslation ? "Show original" : "Translate to Hebrew"}
            </Button>
          </div>
        )}
      </div>

      <TeachMiloDialog
        open={teachOpen}
        onOpenChange={setTeachOpen}
        defaultQuestion={question || ""}
        defaultAnswer={message.content}
        audience={audience}
      />
    </div>
  );
}

