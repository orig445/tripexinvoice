import { useEffect, useRef } from "react";
import { X, RotateCcw } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ChatMessage } from "./ChatMessage";
import { ChatInput } from "./ChatInput";
import { QuickActions } from "./QuickActions";
import { useChatbot } from "@/hooks/useChatbot";
import myloWaving from "@/assets/mylo-waving.jpeg";
import myloThinking from "@/assets/mylo-thinking.jpeg";
import myloSleeping from "@/assets/mylo-sleeping.jpeg";

interface ChatWindowProps {
  onClose: () => void;
}

export function ChatWindow({ onClose }: ChatWindowProps) {
  const { messages, isLoading, config, sendMessage, sendImage, startNewSession } = useChatbot();
  const scrollRef = useRef<HTMLDivElement>(null);
  const cameraInputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    scrollRef.current?.scrollTo({ top: scrollRef.current.scrollHeight, behavior: "smooth" });
  }, [messages, isLoading]);

  const triggerCamera = () => {
    cameraInputRef.current?.click();
  };

  const handleCameraFile = (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (!file) return;
    const reader = new FileReader();
    reader.onload = () => {
      const base64 = (reader.result as string).split(",")[1];
      if (base64) sendImage(base64);
    };
    reader.readAsDataURL(file);
    e.target.value = "";
  };

  const handleAction = (action: string, data?: Record<string, any>) => {
    switch (action) {
      case "Camera":
        triggerCamera();
        break;
      case "Redirect":
        if (data?.redirectPage === "help") {
          sendMessage("אני צריך עזרה");
        } else if (data?.redirectPage === "booking") {
          sendMessage("אני רוצה להגיש בקשת נסיעה");
        } else {
          window.location.href = data?.redirectPage ? `/${data.redirectPage}` : "/";
        }
        break;
      case "AddExpense":
        sendMessage("אני רוצה להוסיף הוצאה");
        break;
      case "DisplayResults":
        break;
    }
  };

  const botName = config?.bot_name || "Mylo AI";
  const welcomeMsg = config?.welcome_message || "שלום! אני מיילו 🦊 איך אוכל לעזור לך היום?";

  return (
    <div className="flex flex-col w-[360px] h-[500px] sm:w-[380px] sm:h-[520px] bg-background rounded-2xl shadow-xl border overflow-hidden animate-in slide-in-from-bottom-4 duration-300">
      {/* Header */}
      <div className="flex items-center justify-between px-4 py-3 bg-gradient-to-r from-primary to-triplex-teal-light text-primary-foreground">
        <div className="flex items-center gap-2.5">
          <div className="w-9 h-9 rounded-full overflow-hidden border-2 border-primary-foreground/30 hover:scale-110 transition-transform duration-200">
            <img src={myloWaving} alt="Mylo" className="w-full h-full object-cover" />
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
        {/* Welcome message with Mylo */}
        {messages.length === 0 && (
          <div className="flex flex-col items-center py-4 gap-3">
            <div className="relative">
              <img
                src={myloWaving}
                alt="Mylo waving"
                className="w-24 h-24 rounded-full border-2 border-primary/20 object-cover animate-in zoom-in duration-500 hover:scale-110 transition-transform duration-300"
              />
              <div className="absolute -bottom-1 -right-1 w-5 h-5 bg-triplex-success rounded-full border-2 border-background animate-pulse" />
              <div className="absolute -top-2 -left-2 text-xl animate-bounce">👋</div>
            </div>
            <div className="bg-muted rounded-2xl px-4 py-3 text-sm text-center max-w-[85%]">
              {welcomeMsg}
            </div>
          </div>
        )}

        {messages.map((msg) => (
          <ChatMessage key={msg.id} message={msg} onAction={handleAction} />
        ))}

        {/* Loading state with thinking Mylo */}
        {isLoading && (
          <div className="flex gap-2.5">
            <div className="w-8 h-8 rounded-full flex-shrink-0 overflow-hidden border border-primary/20">
              <img src={myloThinking} alt="Mylo thinking" className="w-full h-full object-cover animate-[wiggle_1s_ease-in-out_infinite]" />
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
      <ChatInput onSend={sendMessage} onImageCapture={sendImage} isLoading={isLoading} />
      <input
        ref={cameraInputRef}
        type="file"
        accept="image/*"
        capture="environment"
        className="hidden"
        onChange={handleCameraFile}
      />
    </div>
  );
}
