import { useState, useEffect } from "react";
import { supabase } from "@/integrations/supabase/client";
import { Loader2, MessageCircle, ChevronDown, ChevronUp } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";

export function ChatbotSessions() {
  const [sessions, setSessions] = useState<any[]>([]);
  const [expandedSession, setExpandedSession] = useState<string | null>(null);
  const [sessionMessages, setSessionMessages] = useState<any[]>([]);
  const [isLoading, setIsLoading] = useState(true);

  useEffect(() => {
    loadSessions();
  }, []);

  const loadSessions = async () => {
    const { data } = await supabase
      .from("chat_sessions")
      .select("*")
      .order("created_at", { ascending: false })
      .limit(50);
    setSessions(data || []);
    setIsLoading(false);
  };

  const toggleSession = async (sessionId: string) => {
    if (expandedSession === sessionId) {
      setExpandedSession(null);
      return;
    }
    setExpandedSession(sessionId);
    const { data } = await supabase
      .from("chat_messages")
      .select("*")
      .eq("session_id", sessionId)
      .order("created_at", { ascending: true });
    setSessionMessages(data || []);
  };

  if (isLoading) {
    return (
      <div className="flex items-center justify-center py-12">
        <Loader2 className="h-6 w-6 animate-spin text-primary" />
      </div>
    );
  }

  return (
    <div className="space-y-3">
      {sessions.length === 0 ? (
        <p className="text-center text-muted-foreground py-8">אין שיחות עדיין</p>
      ) : (
        sessions.map((session) => (
          <Card key={session.id}>
            <Button
              variant="ghost"
              className="w-full justify-between p-4 h-auto"
              onClick={() => toggleSession(session.id)}
            >
              <div className="flex items-center gap-2">
                <MessageCircle className="h-4 w-4 text-muted-foreground" />
                <span className="text-sm">
                  {new Date(session.created_at).toLocaleString("he-IL")}
                </span>
                <Badge variant="outline" className="text-xs">
                  {session.source}
                </Badge>
                <Badge
                  variant={session.status === "active" ? "default" : "secondary"}
                  className="text-xs"
                >
                  {session.status === "active" ? "פעיל" : "סגור"}
                </Badge>
              </div>
              {expandedSession === session.id ? (
                <ChevronUp className="h-4 w-4" />
              ) : (
                <ChevronDown className="h-4 w-4" />
              )}
            </Button>

            {expandedSession === session.id && (
              <CardContent className="pt-0 space-y-2">
                {sessionMessages.map((msg) => (
                  <div
                    key={msg.id}
                    className={`text-sm p-2 rounded-lg ${
                      msg.role === "user"
                        ? "bg-primary/10 mr-8"
                        : "bg-muted ml-8"
                    }`}
                  >
                    <span className="text-xs font-medium text-muted-foreground">
                      {msg.role === "user" ? "משתמש" : "בוט"}
                      {msg.intent && ` • ${msg.intent}`}
                    </span>
                    <p className="mt-0.5">{msg.content}</p>
                  </div>
                ))}
                {sessionMessages.length === 0 && (
                  <p className="text-xs text-muted-foreground text-center py-4">
                    אין הודעות בשיחה זו
                  </p>
                )}
              </CardContent>
            )}
          </Card>
        ))
      )}
    </div>
  );
}
