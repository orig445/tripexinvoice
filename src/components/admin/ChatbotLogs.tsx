import { useState, useEffect } from "react";
import { supabase } from "@/integrations/supabase/client";
import { Loader2 } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";

export function ChatbotLogs() {
  const [logs, setLogs] = useState<any[]>([]);
  const [isLoading, setIsLoading] = useState(true);

  useEffect(() => {
    loadLogs();
  }, []);

  const loadLogs = async () => {
    const { data } = await supabase
      .from("chatbot_logs")
      .select("*")
      .order("created_at", { ascending: false })
      .limit(100);
    setLogs(data || []);
    setIsLoading(false);
  };

  if (isLoading) {
    return (
      <div className="flex items-center justify-center py-12">
        <Loader2 className="h-6 w-6 animate-spin text-primary" />
      </div>
    );
  }

  const intentColors: Record<string, string> = {
    help: "bg-blue-100 text-blue-800",
    scan: "bg-purple-100 text-purple-800",
    bi: "bg-green-100 text-green-800",
    online: "bg-amber-100 text-amber-800",
    expense: "bg-teal-100 text-teal-800",
    general: "bg-gray-100 text-gray-800",
    error: "bg-red-100 text-red-800",
  };

  return (
    <div className="space-y-4">
      {/* Stats */}
      <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
        {["help", "scan", "expense", "general"].map((intent) => (
          <div key={intent} className="rounded-lg border p-3 text-center">
            <p className="text-2xl font-bold">
              {logs.filter((l) => l.details?.intent === intent).length}
            </p>
            <p className="text-xs text-muted-foreground">{intent}</p>
          </div>
        ))}
      </div>

      {/* Table */}
      <div className="rounded-lg border overflow-hidden">
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>תאריך</TableHead>
              <TableHead>סוג</TableHead>
              <TableHead>כוונה</TableHead>
              <TableHead>פרטים</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {logs.length === 0 ? (
              <TableRow>
                <TableCell colSpan={4} className="text-center text-muted-foreground py-8">
                  אין לוגים עדיין
                </TableCell>
              </TableRow>
            ) : (
              logs.map((log) => (
                <TableRow key={log.id}>
                  <TableCell className="text-xs">
                    {new Date(log.created_at).toLocaleString("he-IL")}
                  </TableCell>
                  <TableCell>
                    <Badge variant="outline" className="text-xs">
                      {log.event_type}
                    </Badge>
                  </TableCell>
                  <TableCell>
                    {log.details?.intent && (
                      <span
                        className={`text-xs px-2 py-0.5 rounded-full ${
                          intentColors[log.details.intent] || intentColors.general
                        }`}
                      >
                        {log.details.intent}
                      </span>
                    )}
                  </TableCell>
                  <TableCell className="text-xs text-muted-foreground max-w-[200px] truncate">
                    {log.details?.message_preview || log.details?.error || "-"}
                  </TableCell>
                </TableRow>
              ))
            )}
          </TableBody>
        </Table>
      </div>
    </div>
  );
}
