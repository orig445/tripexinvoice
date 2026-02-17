import { useState } from "react";
import { MessageCircle } from "lucide-react";
import { ChatWindow } from "./ChatWindow";
import { useAuth } from "@/hooks/useAuth";
import myloWaving from "@/assets/mylo-waving.jpeg";

export function ChatbotWidget() {
  const [isOpen, setIsOpen] = useState(false);
  const { user } = useAuth();

  if (!user) return null;

  return (
    <div className="fixed bottom-5 right-5 z-50">
      {isOpen && <ChatWindow onClose={() => setIsOpen(false)} />}

      {!isOpen && (
        <button
          onClick={() => setIsOpen(true)}
          className="w-16 h-16 rounded-full shadow-lg hover:shadow-xl transition-all duration-300 flex items-center justify-center hover:scale-110 active:scale-95 overflow-hidden border-2 border-primary/30 bg-background"
          aria-label="Open chat"
        >
          <img src={myloWaving} alt="Mylo" className="w-full h-full object-cover" />
        </button>
      )}
    </div>
  );
}
