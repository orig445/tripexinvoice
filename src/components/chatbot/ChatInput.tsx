import { useState, useRef, useEffect } from "react";
import { Send } from "lucide-react";
import { Button } from "@/components/ui/button";

interface ChatInputProps {
  onSend: (message: string) => void;
  isLoading: boolean;
}

export function ChatInput({ onSend, isLoading }: ChatInputProps) {
  const [value, setValue] = useState("");
  const inputRef = useRef<HTMLTextAreaElement>(null);

  useEffect(() => {
    inputRef.current?.focus();
  }, []);

  const handleSubmit = () => {
    if (!value.trim() || isLoading) return;
    onSend(value.trim());
    setValue("");
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === "Enter" && !e.shiftKey) {
      e.preventDefault();
      handleSubmit();
    }
  };

  return (
    <div className="flex items-end gap-2 p-3 border-t bg-background">
      <textarea
        ref={inputRef}
        value={value}
        onChange={(e) => setValue(e.target.value)}
        onKeyDown={handleKeyDown}
        placeholder="כתוב הודעה..."
        rows={1}
        className="flex-1 resize-none rounded-xl border bg-muted/50 px-3 py-2 text-sm focus:outline-none focus:ring-1 focus:ring-primary min-h-[36px] max-h-[100px]"
        disabled={isLoading}
      />
      <Button
        size="icon"
        className="h-9 w-9 rounded-xl flex-shrink-0"
        onClick={handleSubmit}
        disabled={!value.trim() || isLoading}
      >
        <Send className="h-4 w-4" />
      </Button>
    </div>
  );
}
