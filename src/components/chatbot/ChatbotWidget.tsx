import { useState } from "react";
import { MessageCircle, X } from "lucide-react";
import { ChatWindow } from "./ChatWindow";
import { useAuth } from "@/hooks/useAuth";

export function ChatbotWidget() {
  const [isOpen, setIsOpen] = useState(false);
  const { user } = useAuth();

  if (!user) return null;

  return (
    <div className="fixed bottom-5 right-5 z-50" dir="rtl">
      {isOpen && <ChatWindow onClose={() => setIsOpen(false)} />}

      {!isOpen && (
        <button
          onClick={() => setIsOpen(true)}
          className="w-14 h-14 rounded-full bg-gradient-to-br from-primary to-triplex-teal-light text-primary-foreground shadow-lg hover:shadow-xl transition-all duration-300 flex items-center justify-center hover:scale-105 active:scale-95"
          aria-label="פתח צ'אט"
        >
          <MessageCircle className="h-6 w-6" />
        </button>
      )}
    </div>
  );
}
