import { useEffect, useState } from "react";
import { supabase } from "@/integrations/supabase/client";
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/label";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { Switch } from "@/components/ui/switch";
import { RadioGroup, RadioGroupItem } from "@/components/ui/radio-group";
import { Badge } from "@/components/ui/badge";
import { toast } from "sonner";
import { Loader2, RefreshCw, Mail, Send } from "lucide-react";

type Cfg = {
  id: string;
  enabled: boolean;
  mode: "draft" | "auto_reply";
  folder: string;
  signature: string;
  last_run_at: string | null;
};
type EmailRow = {
  id: string;
  message_id: string;
  from_address: string | null;
  from_name: string | null;
  subject: string | null;
  received_at: string | null;
  body_preview: string | null;
  reply_text: string | null;
  status: string;
  error_message: string | null;
  created_at: string;
};

export function OutlookAgent() {
  const [cfg, setCfg] = useState<Cfg | null>(null);
  const [emails, setEmails] = useState<EmailRow[]>([]);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [sendingId, setSendingId] = useState<string | null>(null);

  // Manually (re)generate and SEND a reply for one email — for testing.
  async function sendTestReply(e: EmailRow) {
    setSendingId(e.id);
    const { data, error } = await supabase.functions.invoke("outlook-support-agent", {
      body: { message_id: e.message_id, mode: "auto_reply" },
    });
    setSendingId(null);
    const result = (data as any)?.single;
    if (error || result?.status === "failed") {
      toast.error(`Send failed: ${result?.error || error?.message || "unknown error"}`, { duration: 10000 });
    } else {
      toast.success("Reply sent ✅");
    }
    load();
  }

  async function load() {
    setLoading(true);
    const [{ data: c }, { data: e }] = await Promise.all([
      supabase.from("outlook_agent_config").select("*").limit(1).maybeSingle(),
      supabase
        .from("outlook_processed_emails")
        .select("*")
        .order("created_at", { ascending: false })
        .limit(50),
    ]);
    setCfg(c as Cfg | null);
    setEmails((e as EmailRow[]) ?? []);
    setLoading(false);
  }

  useEffect(() => {
    load();
  }, []);

  async function save() {
    if (!cfg) return;
    setSaving(true);
    const { error } = await supabase
      .from("outlook_agent_config")
      .update({
        enabled: cfg.enabled,
        mode: cfg.mode,
        folder: cfg.folder,
        signature: cfg.signature,
      })
      .eq("id", cfg.id);
    setSaving(false);
    if (error) toast.error("Failed to save: " + error.message);
    else toast.success("Settings saved");
  }


  if (loading || !cfg) {
    return (
      <div className="flex items-center justify-center py-12">
        <Loader2 className="h-6 w-6 animate-spin" />
      </div>
    );
  }

  const statusColor = (s: string) =>
    s === "sent"
      ? "default"
      : s === "draft_created"
      ? "secondary"
      : s === "failed"
      ? "destructive"
      : "outline";

  return (
    <div className="space-y-6" dir="ltr">
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Mail className="h-5 w-5" />
            Outlook Customer Support Agent
          </CardTitle>
          <CardDescription>
            Milo runs automatically every 2 minutes, reading unread emails from your connected Outlook mailbox
            and either drafting a reply or auto-sending it, grounded in your knowledge base.
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-6">
          <div className="flex items-center justify-between">
            <div>
              <Label className="text-base">Agent enabled</Label>
              <p className="text-sm text-muted-foreground">Turn on to allow processing on each run.</p>
            </div>
            <Switch
              checked={cfg.enabled}
              onCheckedChange={(v) => setCfg({ ...cfg, enabled: v })}
            />
          </div>

          <div className="space-y-2">
            <Label>Mode</Label>
            <RadioGroup
              value={cfg.mode}
              onValueChange={(v: any) => setCfg({ ...cfg, mode: v })}
              className="grid grid-cols-1 md:grid-cols-2 gap-3"
            >
              <label className="flex items-start gap-3 border rounded-lg p-3 cursor-pointer hover:bg-accent">
                <RadioGroupItem value="draft" className="mt-1" />
                <div>
                  <div className="font-medium">Draft reply (safe)</div>
                  <div className="text-sm text-muted-foreground">
                    Creates a draft in Outlook — a human reviews before sending.
                  </div>
                </div>
              </label>
              <label className="flex items-start gap-3 border rounded-lg p-3 cursor-pointer hover:bg-accent">
                <RadioGroupItem value="auto_reply" className="mt-1" />
                <div>
                  <div className="font-medium">Auto-reply</div>
                  <div className="text-sm text-muted-foreground">
                    Milo sends the reply automatically and marks the email as read.
                  </div>
                </div>
              </label>
            </RadioGroup>
          </div>

          <div className="grid gap-2">
            <Label>Mail folder</Label>
            <Input
              value={cfg.folder}
              onChange={(e) => setCfg({ ...cfg, folder: e.target.value })}
              placeholder="inbox"
            />
            <p className="text-xs text-muted-foreground">
              Well-known names: inbox, drafts, sentitems, archive.
            </p>
          </div>

          <div className="grid gap-2">
            <Label>Signature</Label>
            <Textarea
              rows={3}
              value={cfg.signature}
              onChange={(e) => setCfg({ ...cfg, signature: e.target.value })}
            />
          </div>

          <div className="flex flex-wrap gap-2 pt-2">
            <Button onClick={save} disabled={saving}>
              {saving && <Loader2 className="h-4 w-4 mr-2 animate-spin" />}
              Save settings
            </Button>
            <Button variant="ghost" onClick={load}>
              <RefreshCw className="h-4 w-4 mr-2" />
              Refresh
            </Button>
            {cfg.last_run_at && (
              <span className="text-sm text-muted-foreground self-center ml-auto">
                Last run: {new Date(cfg.last_run_at).toLocaleString()}
              </span>
            )}
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Recent processed emails</CardTitle>
          <CardDescription>Last 50 emails Milo has handled.</CardDescription>
        </CardHeader>
        <CardContent>
          {emails.length === 0 ? (
            <p className="text-sm text-muted-foreground py-8 text-center">
              No emails processed yet. Enable the agent and click "Run now".
            </p>
          ) : (
            <div className="space-y-3">
              {emails.map((e) => (
                <div key={e.id} className="border rounded-lg p-3 space-y-2">
                  <div className="flex items-start justify-between gap-2">
                    <div className="min-w-0">
                      <div className="font-medium truncate">{e.subject || "(no subject)"}</div>
                      <div className="text-xs text-muted-foreground">
                        From {e.from_name || e.from_address} · {new Date(e.created_at).toLocaleString()}
                      </div>
                    </div>
                    <div className="flex items-center gap-2 flex-shrink-0">
                      <Badge variant={statusColor(e.status) as any}>{e.status}</Badge>
                      <Button
                        size="sm"
                        variant="outline"
                        className="gap-1.5"
                        disabled={sendingId === e.id}
                        onClick={() => sendTestReply(e)}
                        title="Generate a reply now and send it (test)"
                      >
                        {sendingId === e.id ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Send className="h-3.5 w-3.5" />}
                        Send reply
                      </Button>
                    </div>
                  </div>
                  {e.body_preview && (
                    <div className="text-sm bg-muted/50 rounded p-2 line-clamp-2">
                      {e.body_preview}
                    </div>
                  )}
                  {e.reply_text && (
                    <details className="text-sm">
                      <summary className="cursor-pointer text-primary">View Milo's reply</summary>
                      <pre className="mt-2 whitespace-pre-wrap font-sans bg-accent/40 rounded p-2">
                        {e.reply_text}
                      </pre>
                    </details>
                  )}
                  {e.error_message && (
                    <div className="text-xs text-destructive">{e.error_message}</div>
                  )}
                </div>
              ))}
            </div>
          )}
        </CardContent>
      </Card>
    </div>
  );
}
