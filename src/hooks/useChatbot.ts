import { useState, useEffect, useCallback, useRef } from "react";
import { supabase } from "@/integrations/supabase/client";
import { useAuth } from "@/hooks/useAuth";
import { toast } from "sonner";
import { sendChatMessage, sendImageForScan, type KnowledgeAudience } from "@/lib/api-service";

export interface ChatMessage {
  id: string;
  role: "user" | "assistant" | "system";
  content: string;
  intent?: string;
  metadata?: {
    actions?: string[];
    redirectPage?: string;
    data?: Record<string, any>;
    sources?: Array<{ name: string; url: string | null }>;
    [key: string]: any;
  };
  created_at: string;
}

interface ChatbotConfig {
  bot_name: string;
  avatar_url: string | null;
  welcome_message: string;
  is_active: boolean;
}

export function useChatbot(options?: { audience?: KnowledgeAudience; source?: string }) {
  const audience: KnowledgeAudience = options?.audience || "external";
  const source = options?.source || "web";
  const { user } = useAuth();
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [sessionId, setSessionId] = useState<string | null>(null);
  const [isLoading, setIsLoading] = useState(false);
  const [config, setConfig] = useState<ChatbotConfig | null>(null);
  const [sessions, setSessions] = useState<
    { id: string; title: string; updated_at: string; status: string }[]
  >([]);


  // Mirror of sessionId that updates SYNCHRONOUSLY. When several receipts are
  // sent in one batch (before React re-renders), each call must reuse the
  // session created by the first one instead of each opening its own session.
  const sessionIdRef = useRef<string | null>(null);
  useEffect(() => {
    sessionIdRef.current = sessionId;
  }, [sessionId]);

  const loadConfig = async () => {
    const { data } = await supabase
      .from("chatbot_config")
      .select("bot_name, avatar_url, welcome_message, is_active")
      .eq("is_active", true)
      .limit(1)
      .single();
    if (data) setConfig(data as ChatbotConfig);
  };

  useEffect(() => {
    loadConfig();
  }, []);

  // Restore the user's most recent conversation (per user + source) so the
  // history survives reloads and navigation between pages.
  useEffect(() => {
    if (!user || sessionIdRef.current) return;
    let cancelled = false;
    const restoreSession = async () => {
      const { data } = await supabase
        .from("chat_sessions")
        .select("id")
        .eq("user_id", user.id)
        .eq("source", source)
        .eq("status", "active")
        .order("updated_at", { ascending: false })
        .limit(1)
        .maybeSingle();
      if (cancelled || !data?.id || sessionIdRef.current) return;
      sessionIdRef.current = data.id;
      setSessionId(data.id);
    };
    restoreSession();
    return () => {
      cancelled = true;
    };
  }, [user?.id, source]);

  useEffect(() => {
    if (!sessionId) return;
    const loadMessages = async () => {
      const { data } = await supabase
        .from("chat_messages")
        .select("*")
        .eq("session_id", sessionId)
        .order("created_at", { ascending: true });
      // Only hydrate from the DB when we don't already have messages on screen.
      // Otherwise a freshly-created session would wipe the optimistic messages
      // of receipts still being processed in the same batch.
      if (data && data.length > 0) {
        setMessages((prev) => (prev.length > 0 ? prev : (data as ChatMessage[])));
      }
    };
    loadMessages();
  }, [sessionId]);


  const sendMessage = useCallback(
    async (text: string) => {
      if (!user || !text.trim()) return;
      setIsLoading(true);

      const tempId = crypto.randomUUID();
      const tempMsg: ChatMessage = {
        id: tempId,
        role: "user",
        content: text,
        created_at: new Date().toISOString(),
      };
      setMessages((prev) => [...prev, tempMsg]);

      try {
        const now = new Date();
        const userLocalDate = now.toLocaleDateString("en-US", { weekday: "long", year: "numeric", month: "long", day: "numeric" });
        const userLocalTime = now.toLocaleTimeString("en-US", { hour: "2-digit", minute: "2-digit" });
        const timezone = Intl.DateTimeFormat().resolvedOptions().timeZone;

        const { data, error } = await sendChatMessage({
          text, source, sessionToken: sessionIdRef.current, userDate: userLocalDate, userTime: userLocalTime, userTimezone: timezone, audience,
        });

        if (error) throw error;

        if (data.session_id && !sessionIdRef.current) {
          sessionIdRef.current = data.session_id;
          setSessionId(data.session_id);
        }

        const assistantMsg: ChatMessage = {
          id: crypto.randomUUID(),
          role: "assistant",
          content: data.text,
          metadata: {
            actions: data.actions || [],
            redirectPage: data.redirectPage || "",
            data: data.data || {},
            sources: data.data?.sources || [],
          },
          created_at: new Date().toISOString(),
        };
        setMessages((prev) => [...prev, assistantMsg]);

        return { actions: data.actions, redirectPage: data.redirectPage, data: data.data };
      } catch (err: any) {
        console.error("Chat error:", err);
        toast.error("Error sending message");
        setMessages((prev) => prev.filter((m) => m.id !== tempId));
      } finally {
        setIsLoading(false);
      }
    },
    [user, sessionId]
  );

  const sendImage = useCallback(
    async (base64: string) => {
      if (!user) return;
      setIsLoading(true);

      const tempMsg: ChatMessage = {
        id: crypto.randomUUID(),
        role: "user",
        content: "📷 Scanning invoice...",
        created_at: new Date().toISOString(),
      };
      setMessages((prev) => [...prev, tempMsg]);

      try {
        const { data, error } = await sendImageForScan({
          base64, source, sessionToken: sessionIdRef.current, audience,
        });

        if (error) throw error;

        if (data.session_id && !sessionIdRef.current) {
          sessionIdRef.current = data.session_id;
          setSessionId(data.session_id);
        }

        const assistantMsg: ChatMessage = {
          id: crypto.randomUUID(),
          role: "assistant",
          content: data.text || "Invoice scanned successfully!",
          metadata: {
            actions: data.actions || [],
            redirectPage: data.redirectPage || "",
            data: data.data || {},
          },
          created_at: new Date().toISOString(),
        };
        setMessages((prev) => [...prev, assistantMsg]);

        return { actions: data.actions, data: data.data };
      } catch (err: any) {
        console.error("Image scan error:", err);
        toast.error("Error scanning invoice");
        setMessages((prev) => prev.filter((m) => m.id !== tempMsg.id));
      } finally {
        setIsLoading(false);
      }
    },
    [user, sessionId]
  );

  const loadSessions = useCallback(async () => {
    if (!user) return;
    const { data: sessionRows } = await supabase
      .from("chat_sessions")
      .select("id, created_at, updated_at, status")
      .eq("user_id", user.id)
      .eq("source", source)
      .order("updated_at", { ascending: false })
      .limit(30);
    if (!sessionRows || sessionRows.length === 0) {
      setSessions([]);
      return;
    }
    const ids = sessionRows.map((s) => s.id);
    const { data: msgRows } = await supabase
      .from("chat_messages")
      .select("session_id, content, role, created_at")
      .in("session_id", ids)
      .eq("role", "user")
      .order("created_at", { ascending: true });

    const firstBySession = new Map<string, string>();
    (msgRows || []).forEach((m: any) => {
      if (!firstBySession.has(m.session_id)) firstBySession.set(m.session_id, m.content);
    });

    setSessions(
      sessionRows.map((s: any) => ({
        id: s.id,
        title: (firstBySession.get(s.id) || "New conversation").slice(0, 60),
        updated_at: s.updated_at,
        status: s.status,
      }))
    );
  }, [user?.id, source]);

  useEffect(() => {
    loadSessions();
  }, [loadSessions, sessionId]);

  const loadSession = useCallback(async (id: string) => {
    sessionIdRef.current = id;
    setSessionId(id);
    setMessages([]);
    const { data } = await supabase
      .from("chat_messages")
      .select("*")
      .eq("session_id", id)
      .order("created_at", { ascending: true });
    setMessages((data as ChatMessage[]) || []);
    await supabase.from("chat_sessions").update({ status: "active" }).eq("id", id);
  }, []);

  const startNewSession = useCallback(() => {
    const previous = sessionIdRef.current;
    sessionIdRef.current = null;
    setSessionId(null);
    setMessages([]);
    // Archive the previous conversation so it is kept in history but not
    // restored automatically next time.
    if (previous) {
      supabase
        .from("chat_sessions")
        .update({ status: "closed" })
        .eq("id", previous)
        .then(({ error }) => {
          if (error) console.error("Failed to close session:", error);
          loadSessions();
        });
    }
  }, [loadSessions]);

  return {
    messages,
    isLoading,
    config,
    sessionId,
    sessions,
    loadSessions,
    loadSession,
    sendMessage,
    sendImage,
    startNewSession,
  };
}

