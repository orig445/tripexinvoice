import { useEffect, useRef, useState } from "react";
import { supabase } from "@/integrations/supabase/client";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Card } from "@/components/ui/card";
import { Loader2, MessageSquare, Send, X, Sparkles } from "lucide-react";
import { toast } from "sonner";

export interface QaBotRow {
  date: string;
  user: string;
  source: string;
  intent: string | null;
  question: string;
  answer: string;
}

interface Props {
  rows: QaBotRow[];
  stats: Record<string, unknown>;
}

interface Msg {
  role: "user" | "assistant";
  content: string;
}

const SUGGESTIONS = [
  "What are the most common topics users ask about?",
  "Which questions were left unanswered?",
  "Summarize the main pain points in this data",
];

export function QaInsightsBot({ rows, stats }: Props) {
  const [open, setOpen] = useState(false);
  const [input, setInput] = useState("");
  const [messages, setMessages] = useState<Msg[]>([]);
  const [isLoading, setIsLoading] = useState(false);
  const bottomRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [messages, isLoading]);

  useEffect(() => {
    if (open) inputRef.current?.focus();
  }, [open]);

  const ask = async (text: string) => {
    const question = text.trim();
    if (!question || isLoading) return;
    const history = messages;
    setMessages((prev) => [...prev, { role: "user", content: question }]);
    setInput("");
    setIsLoading(true);
    try {
      const { data, error } = await supabase.functions.invoke("qa-insights", {
        body: { question, rows, stats, history },
      });
      if (error) throw error;
      setMessages((prev) => [
        ...prev,
        { role: "assistant", content: data?.text || "No answer returned." },
      ]);
    } catch (err) {
      console.error("qa-insights error:", err);
      toast.error("Could not analyze the data right now");
      setMessages((prev) => prev.slice(0, -1));
    } finally {
      setIsLoading(false);
      inputRef.current?.focus();
    }
  };

  if (!open) {
    return (
      <button
        onClick={() => setOpen(true)}
        className="fixed bottom-5 right-5 z-50 h-14 rounded-full px-5 bg-primary text-primary-foreground shadow-lg hover:shadow-xl transition-all hover:scale-105 active:scale-95 flex items-center gap-2"
        aria-label="Ask about this data"
      >
        <Sparkles className="h-5 w-5" />
        <span className="text-sm font-medium hidden sm:inline">Ask about this data</span>
      </button>
    );
  }

  return (
    <Card className="fixed bottom-5 right-5 z-50 w-[min(92vw,420px)] h-[min(75vh,560px)] flex flex-col overflow-hidden shadow-2xl">
      <div className="flex items-center justify-between px-4 py-3 border-b bg-primary text-primary-foreground">
        <div className="flex items-center gap-2">
          <MessageSquare className="h-4 w-4" />
          <div>
            <p className="text-sm font-semibold leading-none">Data assistant</p>
            <p className="text-[11px] opacity-80 mt-1">{rows.length} filtered Q&amp;A rows</p>
          </div>
        </div>
        <Button
          variant="ghost"
          size="icon"
          className="h-8 w-8 text-primary-foreground hover:bg-primary-foreground/20"
          onClick={() => setOpen(false)}
          aria-label="Close"
        >
          <X className="h-4 w-4" />
        </Button>
      </div>

      <div className="flex-1 overflow-y-auto p-3 space-y-3">
        {messages.length === 0 && (
          <div className="space-y-2">
            <p className="text-sm text-muted-foreground">
              Ask anything about the questions and answers currently shown in the dashboard.
            </p>
            {SUGGESTIONS.map((s) => (
              <button
                key={s}
                onClick={() => ask(s)}
                className="w-full text-left text-sm rounded-lg border px-3 py-2 hover:bg-muted transition-colors"
              >
                {s}
              </button>
            ))}
          </div>
        )}

        {messages.map((m, i) => (
          <div
            key={i}
            className={
              m.role === "user"
                ? "ml-auto max-w-[85%] rounded-2xl bg-primary text-primary-foreground px-3 py-2 text-sm whitespace-pre-wrap"
                : "mr-auto max-w-[90%] rounded-2xl bg-muted px-3 py-2 text-sm whitespace-pre-wrap"
            }
          >
            {m.content}
          </div>
        ))}

        {isLoading && (
          <div className="mr-auto rounded-2xl bg-muted px-3 py-2">
            <Loader2 className="h-4 w-4 animate-spin text-muted-foreground" />
          </div>
        )}
        <div ref={bottomRef} />
      </div>

      <form
        onSubmit={(e) => {
          e.preventDefault();
          ask(input);
        }}
        className="border-t p-2 flex items-center gap-2"
      >
        <Input
          ref={inputRef}
          value={input}
          onChange={(e) => setInput(e.target.value)}
          placeholder="Ask about this data..."
          disabled={isLoading}
        />
        <Button type="submit" size="icon" disabled={isLoading || !input.trim()}>
          <Send className="h-4 w-4" />
        </Button>
      </form>
    </Card>
  );
}
