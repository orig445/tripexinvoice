import { useEffect, useRef } from "react";
import { RotateCcw, Lock } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Header } from "@/components/Header";
import { ChatMessage } from "@/components/chatbot/ChatMessage";
import { ChatInput } from "@/components/chatbot/ChatInput";
import { useChatbot } from "@/hooks/useChatbot";

/**
 * Internal staff chatbot (route: /chat-internal).
 *
 * Same conversational engine as the customer chat, but every request is sent
 * with audience:"internal" (and source:"internal"), so the RAG layer retrieves
 * ONLY documents from the internal knowledge base (/knowledge-internal) and
 * never the customer-facing ones.
 */
const ChatInternal = () => {
  // audience:"internal" routes retrieval to the internal knowledge base.
  const { messages, isLoading, sendMessage, sendImage, startNewSession } = useChatbot({
    audience: "internal",
    source: "internal",
  });
  const scrollRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    scrollRef.current?.scrollTo({ top: scrollRef.current.scrollHeight, behavior: "smooth" });
  }, [messages, isLoading]);

  const handleAction = (action: string, data?: Record<string, any>) => {
    if (action === "Redirect" && data?.redirectPage) {
      window.location.href = `/${data.redirectPage}`;
    }
  };

  return (
    <div className="min-h-screen flex flex-col bg-background" dir="rtl">
      <Header />

      {/* Chat header — amber theme marks this as the internal assistant */}
      <div className="border-b bg-gradient-to-r from-amber-600 to-amber-500 text-white">
        <div className="container flex items-center justify-between py-4">
          <div className="flex items-center gap-3">
            <div className="w-12 h-12 rounded-full bg-white/15 flex items-center justify-center border-2 border-white/30">
              <Lock className="h-6 w-6" />
            </div>
            <div>
              <h2 className="text-lg font-semibold">עוזר פנימי — צוות TripEx</h2>
              <p className="text-xs opacity-80">מבוסס על בסיס הידע הפנימי · לעובדים בלבד</p>
            </div>
          </div>
          <Button
            variant="ghost"
            size="sm"
            className="text-white/90 hover:text-white hover:bg-white/10 gap-2"
            onClick={startNewSession}
          >
            <RotateCcw className="h-4 w-4" />
            שיחה חדשה
          </Button>
        </div>
      </div>

      {/* Messages */}
      <main className="flex-1 overflow-hidden">
        <div ref={scrollRef} className="h-full overflow-y-auto">
          <div className="container max-w-3xl py-6 space-y-4">
            {messages.length === 0 && (
              <div className="flex flex-col items-center py-12 gap-4 text-center">
                <div className="w-20 h-20 rounded-full bg-amber-500/10 flex items-center justify-center">
                  <Lock className="h-9 w-9 text-amber-600" />
                </div>
                <div className="bg-muted rounded-2xl px-5 py-3 text-sm max-w-[85%]">
                  שלום! אני העוזר הפנימי של הצוות. שאלו אותי כל דבר על סמך המסמכים הפנימיים
                  שהועלו למאגר הפנימי.
                </div>
              </div>
            )}

            {messages.map((msg) => (
              <ChatMessage key={msg.id} message={msg} onAction={handleAction} />
            ))}

            {isLoading && (
              <div className="flex gap-2.5">
                <div className="w-8 h-8 rounded-full flex-shrink-0 bg-amber-500/10 flex items-center justify-center">
                  <Lock className="h-4 w-4 text-amber-600" />
                </div>
                <div className="bg-muted rounded-2xl rounded-tr-sm px-4 py-3">
                  <div className="flex gap-1">
                    <span className="w-1.5 h-1.5 bg-muted-foreground/50 rounded-full animate-bounce" style={{ animationDelay: "0ms" }} />
                    <span className="w-1.5 h-1.5 bg-muted-foreground/50 rounded-full animate-bounce" style={{ animationDelay: "150ms" }} />
                    <span className="w-1.5 h-1.5 bg-muted-foreground/50 rounded-full animate-bounce" style={{ animationDelay: "300ms" }} />
                  </div>
                </div>
              </div>
            )}
          </div>
        </div>
      </main>

      {/* Input */}
      <div className="border-t bg-background">
        <div className="container max-w-3xl">
          <ChatInput onSend={sendMessage} onImageCapture={sendImage} isLoading={isLoading} />
        </div>
      </div>
    </div>
  );
};

export default ChatInternal;
