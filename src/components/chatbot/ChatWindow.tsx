import { useEffect, useRef } from "react";
import { X, RotateCcw, Maximize2, Minimize2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ChatMessage } from "./ChatMessage";
import { ChatInput } from "./ChatInput";
import { QuickActions } from "./QuickActions";
import { useChatbot } from "@/hooks/useChatbot";
import { pdfAllPagesToBase64 } from "@/lib/pdf-utils";
import myloWaving from "@/assets/mylo-waving.jpeg";
import myloThinking from "@/assets/mylo-thinking.jpeg";

interface ChatWindowProps {
  onClose: () => void;
  isFullscreen?: boolean;
  onToggleFullscreen?: () => void;
}

export function ChatWindow({ onClose, isFullscreen = false, onToggleFullscreen }: ChatWindowProps) {
  const { messages, isLoading, config, sendMessage, sendImage, startNewSession } = useChatbot();
  const scrollRef = useRef<HTMLDivElement>(null);
  const cameraInputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    scrollRef.current?.scrollTo({ top: scrollRef.current.scrollHeight, behavior: "smooth" });
  }, [messages, isLoading]);

  const triggerCamera = () => {
    cameraInputRef.current?.click();
  };

  const compressImage = (file: File, maxWidth = 1200, quality = 0.7): Promise<string> => {
    return new Promise((resolve, reject) => {
      const img = new Image();
      const url = URL.createObjectURL(file);
      img.onload = () => {
        URL.revokeObjectURL(url);
        const scale = Math.min(1, maxWidth / img.width);
        const canvas = document.createElement("canvas");
        canvas.width = img.width * scale;
        canvas.height = img.height * scale;
        const ctx = canvas.getContext("2d");
        if (!ctx) return reject(new Error("Canvas not supported"));
        ctx.drawImage(img, 0, 0, canvas.width, canvas.height);
        const dataUrl = canvas.toDataURL("image/jpeg", quality);
        const base64 = dataUrl.split(",")[1];
        if (base64) resolve(base64);
        else reject(new Error("Failed to compress"));
      };
      img.onerror = () => { URL.revokeObjectURL(url); reject(new Error("Failed to load image")); };
      img.src = url;
    });
  };

  const handleCameraFile = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (!file) return;
    e.target.value = "";
    try {
      if (file.type === "application/pdf") {
        const pages = await pdfAllPagesToBase64(file);
        if (pages.length > 0) sendImage(pages[0]);
        return;
      }

      const base64 = await compressImage(file);
      sendImage(base64);
    } catch (err) {
      console.error("File processing error:", err);
    }
  };

  const handleAction = (action: string, data?: Record<string, any>) => {
    switch (action) {
      case "Camera":
        triggerCamera();
        break;
      case "Redirect":
        if (data?.redirectPage === "help") {
          sendMessage("I need help");
        } else if (data?.redirectPage === "booking") {
          sendMessage("I want to submit a travel request");
        } else {
          window.location.href = data?.redirectPage ? `/${data.redirectPage}` : "/";
        }
        break;
      case "AddExpense":
        sendMessage("I want to add an expense");
        break;
      case "DisplayResults":
        break;
    }
  };

  const botName = config?.bot_name || "Milo AI";
  const welcomeMsg = config?.welcome_message || "Hello! I'm Milo 🦊 How can I help you today?";

  return (
    <div className="flex flex-col w-[360px] h-[500px] sm:w-[380px] sm:h-[520px] bg-background rounded-2xl shadow-xl border overflow-hidden animate-in slide-in-from-bottom-4 duration-300">
      {/* Header */}
      <div className="flex items-center justify-between px-4 py-3 bg-gradient-to-r from-primary to-triplex-teal-light text-primary-foreground">
        <div className="flex items-center gap-2.5">
          <div className="w-9 h-9 rounded-full overflow-hidden border-2 border-primary-foreground/30 hover:scale-110 transition-transform duration-200">
            <img src={myloWaving} alt="Milo" className="w-full h-full object-cover" />
          </div>
          <div>
            <h3 className="text-sm font-semibold">{botName}</h3>
            <p className="text-[10px] opacity-80">Online</p>
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
      <div ref={scrollRef} className="flex-1 overflow-y-auto p-3 space-y-3">
        {messages.length === 0 && (
          <div className="flex flex-col items-center py-4 gap-3">
            <div className="relative">
              <img
                src={myloWaving}
                alt="Milo waving"
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

        {isLoading && (
          <div className="flex gap-2.5">
            <div className="w-8 h-8 rounded-full flex-shrink-0 overflow-hidden border border-primary/20">
              <img src={myloThinking} alt="Milo thinking" className="w-full h-full object-cover animate-[wiggle_1s_ease-in-out_infinite]" />
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

      {messages.length === 0 && <QuickActions onAction={(text) => sendMessage(text)} />}

      <ChatInput onSend={sendMessage} onImageCapture={sendImage} isLoading={isLoading} />
      <input
        ref={cameraInputRef}
        type="file"
        accept="image/*,application/pdf"
        className="hidden"
        onChange={handleCameraFile}
      />
    </div>
  );
}
