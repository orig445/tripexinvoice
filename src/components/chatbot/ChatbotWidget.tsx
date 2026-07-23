import { useState } from "react";
import { ChatWindow } from "./ChatWindow";
import { useAuth } from "@/hooks/useAuth";
import myloWaving from "@/assets/mylo-waving.jpeg";

export function ChatbotWidget() {
  const [isOpen, setIsOpen] = useState(false);
  const [isFullscreen, setIsFullscreen] = useState(false);
  const { user } = useAuth();

  if (!user) return null;

  if (isOpen && isFullscreen) {
    return (
      <div className="fixed inset-0 z-50 bg-background/80 backdrop-blur-sm flex items-center justify-center p-0 sm:p-4">
        <ChatWindow
          onClose={() => { setIsOpen(false); setIsFullscreen(false); }}
          isFullscreen
          onToggleFullscreen={() => setIsFullscreen(false)}
        />
      </div>
    );
  }

  return (
    <div className="fixed bottom-5 right-5 z-50">
      {isOpen && (
        <ChatWindow
          onClose={() => setIsOpen(false)}
          isFullscreen={false}
          onToggleFullscreen={() => setIsFullscreen(true)}
        />
      )}

      {!isOpen && (
        <button
          onClick={() => setIsOpen(true)}
          className="w-16 h-16 rounded-full shadow-lg hover:shadow-xl transition-all duration-300 flex items-center justify-center hover:scale-110 active:scale-95 overflow-hidden border-2 border-primary/30 bg-background"
          aria-label="Open chat"
        >
          <img src={myloWaving} alt="Milo" className="w-full h-full object-cover" />
        </button>
      )}
    </div>
  );
}
