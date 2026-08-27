import { useEffect, useMemo, useState } from "react";
import { supabase } from "@/integrations/supabase/client";
import { Header } from "@/components/Header";
import { Card, CardContent } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Calendar } from "@/components/ui/calendar";
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from "@/components/ui/popover";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { Loader2, MessageSquare, HelpCircle, Bot, Download, RefreshCw, CalendarIcon, X } from "lucide-react";
import { format, startOfDay, endOfDay } from "date-fns";
import { cn } from "@/lib/utils";
import {
  ResponsiveContainer,
  BarChart,
  Bar,
  XAxis,
  YAxis,
  Tooltip as RTooltip,
  CartesianGrid,
  LineChart,
  Line,
  PieChart,
  Pie,
  Cell,
  Legend,
} from "recharts";

interface QaPair {
  id: string;
  sessionId: string;
  source: string;
  intent: string | null;
  question: string;
  answer: string;
  askedAt: string;
  answeredAt: string | null;
  userId: string | null;
  userLabel: string;
}

const PAGE_SIZE = 1000;
const MAX_MESSAGES = 20000;
const CHART_COLORS = [
  "hsl(var(--primary))",
  "hsl(var(--triplex-info, 200 80% 50%))",
  "hsl(var(--triplex-amber, 38 92% 50%))",
  "hsl(var(--triplex-success, 152 60% 40%))",
  "hsl(var(--muted-foreground))",
];

export default function QaDashboard() {
  const [pairs, setPairs] = useState<QaPair[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [search, setSearch] = useState("");
  const [questionFilter, setQuestionFilter] = useState("");
  const [answerFilter, setAnswerFilter] = useState("");
  const [source, setSource] = useState("all");
  const [intent, setIntent] = useState("all");
  const [status, setStatus] = useState("all");
  const [user, setUser] = useState("all");
  const [dateRange, setDateRange] = useState<{ from?: Date; to?: Date }>({});


  const load = async () => {
    setIsLoading(true);

    // Fetch newest-first in pages so the dashboard always reflects recent data
    const messages: any[] = [];
    for (let offset = 0; offset < MAX_MESSAGES; offset += PAGE_SIZE) {
      const { data, error } = await supabase
        .from("chat_messages")
        .select("id, session_id, role, content, intent, created_at")
        .order("created_at", { ascending: false })
        .range(offset, offset + PAGE_SIZE - 1);
      if (error || !data || data.length === 0) break;
      messages.push(...data);
      if (data.length < PAGE_SIZE) break;
    }

    const sessions: any[] = [];
    for (let offset = 0; offset < MAX_MESSAGES; offset += PAGE_SIZE) {
      const { data, error } = await supabase
        .from("chat_sessions")
        .select("id, source, user_id")
        .range(offset, offset + PAGE_SIZE - 1);
      if (error || !data || data.length === 0) break;
      sessions.push(...data);
      if (data.length < PAGE_SIZE) break;
    }

    const { data: profiles } = await supabase
      .from("profiles")
      .select("user_id, email, display_name");

    const userLabelById = new Map<string, string>(
      (profiles || []).map((p: any) => [
        p.user_id,
        p.email || p.display_name || p.user_id.slice(0, 8),
      ])
    );

    const sourceById = new Map<string, string>(
      sessions.map((s: any) => [s.id, s.source || "web"])
    );
    const userIdBySession = new Map<string, string | null>(
      sessions.map((s: any) => [s.id, s.user_id || null])
    );


    // Restore chronological order inside each session
    messages.sort((a, b) => +new Date(a.created_at) - +new Date(b.created_at));

    const bySession = new Map<string, any[]>();
    messages.forEach((m: any) => {
      const list = bySession.get(m.session_id) || [];
      list.push(m);
      bySession.set(m.session_id, list);
    });

    const isOcrPair = (question: string, answer: string, intent: string | null) => {
      return (
        intent === "scan" ||
        question.toLowerCase().includes("[user scanned an invoice/receipt]") ||
        answer.toLowerCase().includes("invoice scanned successfully") ||
        answer.toLowerCase().includes("scanned invoice")
      );
    };

    const result: QaPair[] = [];
    bySession.forEach((list, sessionId) => {
      const uid = userIdBySession.get(sessionId) || null;
      list.forEach((msg, i) => {
        if (msg.role !== "user") return;
        const reply = list.slice(i + 1).find((m) => m.role !== "user");
        const intent = reply?.intent || msg.intent || null;
        const question = msg.content || "";
        const answer = reply?.content || "";
        if (isOcrPair(question, answer, intent)) return;
        result.push({
          id: msg.id,
          sessionId,
          source: sourceById.get(sessionId) || "web",
          intent,
          question,
          answer,
          askedAt: msg.created_at,
          answeredAt: reply?.created_at || null,
          userId: uid,
          userLabel: uid ? userLabelById.get(uid) || uid.slice(0, 8) : "Anonymous",
        });
      });
    });


    result.sort((a, b) => +new Date(b.askedAt) - +new Date(a.askedAt));
    setPairs(result);
    setIsLoading(false);
  };

  useEffect(() => {
    load();
  }, []);

  const sources = useMemo(
    () => Array.from(new Set(pairs.map((p) => p.source))).sort(),
    [pairs]
  );
  const intents = useMemo(
    () => Array.from(new Set(pairs.map((p) => p.intent).filter(Boolean) as string[])).sort(),
    [pairs]
  );
  const users = useMemo(
    () => Array.from(new Set(pairs.map((p) => p.userLabel))).sort(),
    [pairs]
  );


  const filtered = useMemo(() => {
    const q = search.trim().toLowerCase();
    const qf = questionFilter.trim().toLowerCase();
    const af = answerFilter.trim().toLowerCase();

    // Normalize an inverted range so filtering never silently returns nothing
    const rawFrom = dateRange.from;
    const rawTo = dateRange.to ?? dateRange.from;
    const fromTime =
      rawFrom && rawTo
        ? startOfDay(rawFrom < rawTo ? rawFrom : rawTo).getTime()
        : rawFrom
        ? startOfDay(rawFrom).getTime()
        : null;
    const toTime =
      rawFrom && rawTo
        ? endOfDay(rawFrom < rawTo ? rawTo : rawFrom).getTime()
        : rawTo
        ? endOfDay(rawTo).getTime()
        : null;

    return pairs.filter((p) => {
      if (source !== "all" && p.source !== source) return false;
      if (intent !== "all" && (p.intent || "none") !== intent) return false;
      if (user !== "all" && p.userLabel !== user) return false;
      if (status === "answered" && !p.answer) return false;
      if (status === "unanswered" && p.answer) return false;

      const askedTime = new Date(p.askedAt).getTime();
      if (fromTime !== null && askedTime < fromTime) return false;
      if (toTime !== null && askedTime > toTime) return false;

      if (qf && !p.question.toLowerCase().includes(qf)) return false;
      if (af && !p.answer.toLowerCase().includes(af)) return false;

      if (!q) return true;
      return (
        p.question.toLowerCase().includes(q) ||
        p.answer.toLowerCase().includes(q) ||
        p.source.toLowerCase().includes(q) ||
        p.userLabel.toLowerCase().includes(q) ||
        (p.intent || "").toLowerCase().includes(q)
      );
    });
  }, [pairs, search, questionFilter, answerFilter, source, intent, status, user, dateRange]);


  const unanswered = filtered.filter((p) => !p.answer).length;
  const sessionCount = new Set(filtered.map((p) => p.sessionId)).size;

  const byDay = useMemo(() => {
    const map = new Map<string, number>();
    filtered.forEach((p) => {
      const key = format(new Date(p.askedAt), "yyyy-MM-dd");
      map.set(key, (map.get(key) || 0) + 1);
    });
    return Array.from(map.entries())
      .sort((a, b) => a[0].localeCompare(b[0]))
      .map(([day, count]) => ({ day: format(new Date(day), "dd/MM"), count }));
  }, [filtered]);

  const byIntent = useMemo(() => {
    const map = new Map<string, number>();
    filtered.forEach((p) => {
      const key = p.intent || "no intent";
      map.set(key, (map.get(key) || 0) + 1);
    });
    return Array.from(map.entries())
      .sort((a, b) => b[1] - a[1])
      .slice(0, 10)
      .map(([name, count]) => ({ name, count }));
  }, [filtered]);

  const bySource = useMemo(() => {
    const map = new Map<string, number>();
    filtered.forEach((p) => map.set(p.source, (map.get(p.source) || 0) + 1));
    return Array.from(map.entries()).map(([name, value]) => ({ name, value }));
  }, [filtered]);

  const byUser = useMemo(() => {
    const map = new Map<string, number>();
    filtered.forEach((p) => map.set(p.userLabel, (map.get(p.userLabel) || 0) + 1));
    return Array.from(map.entries())
      .sort((a, b) => b[1] - a[1])
      .slice(0, 10)
      .map(([name, count]) => ({ name, count }));
  }, [filtered]);


  const exportCsv = () => {
    const esc = (v: string) => `"${(v || "").replace(/"/g, '""')}"`;
    const rows = [
      ["Date", "User", "Source", "Intent", "Question", "Answer"].join(","),
      ...filtered.map((p) =>
        [
          esc(new Date(p.askedAt).toISOString()),
          esc(p.userLabel),
          esc(p.source),
          esc(p.intent || ""),
          esc(p.question),
          esc(p.answer),
        ].join(",")
      ),

    ].join("\n");

    const url = URL.createObjectURL(new Blob(["\uFEFF" + rows], { type: "text/csv;charset=utf-8" }));
    const a = document.createElement("a");
    a.href = url;
    a.download = `milo-qa-${new Date().toISOString().slice(0, 10)}.csv`;
    a.click();
    URL.revokeObjectURL(url);
  };

  const stats = [
    { label: "Questions", value: filtered.length, icon: HelpCircle, color: "text-primary", bg: "bg-primary/10" },
    { label: "Conversations", value: sessionCount, icon: MessageSquare, color: "text-triplex-info", bg: "bg-triplex-info/10" },
    { label: "Answered", value: filtered.length - unanswered, icon: Bot, color: "text-triplex-success", bg: "bg-triplex-success/10" },
    { label: "Unanswered", value: unanswered, icon: HelpCircle, color: "text-triplex-amber", bg: "bg-triplex-amber/10" },
  ];

  return (
    <div className="min-h-screen bg-background">
      <Header />
      <main className="container py-6 md:py-10 space-y-6">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div>
            <h1 className="text-2xl font-bold">Q&amp;A Dashboard</h1>
            <p className="text-muted-foreground">
              Every question users asked Milo, with the answer that was given.
            </p>
          </div>
          <div className="flex gap-2">
            <Button variant="outline" size="sm" className="gap-2" onClick={load}>
              <RefreshCw className="h-4 w-4" />
              Refresh
            </Button>
            <Button size="sm" className="gap-2" onClick={exportCsv} disabled={!filtered.length}>
              <Download className="h-4 w-4" />
              Export CSV
            </Button>
          </div>
        </div>

        <div className="grid grid-cols-2 lg:grid-cols-4 gap-4">
          {stats.map((s) => (
            <Card key={s.label}>
              <CardContent className="p-4 md:p-6">
                <div className="flex items-center gap-4">
                  <div className={`w-12 h-12 rounded-xl ${s.bg} flex items-center justify-center shrink-0`}>
                    <s.icon className={`h-6 w-6 ${s.color}`} />
                  </div>
                  <div className="min-w-0">
                    <p className="text-sm text-muted-foreground truncate">{s.label}</p>
                    <p className="text-2xl font-bold">{s.value}</p>
                  </div>
                </div>
              </CardContent>
            </Card>
          ))}
        </div>

        <div className="flex flex-wrap gap-3 items-center">
          <Input
            placeholder="Search questions or answers..."
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            className="max-w-sm"
          />
          <Select value={source} onValueChange={setSource}>
            <SelectTrigger className="w-[160px]">
              <SelectValue placeholder="Source" />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="all">All sources</SelectItem>
              {sources.map((s) => (
                <SelectItem key={s} value={s}>
                  {s}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
          <Select value={intent} onValueChange={setIntent}>
            <SelectTrigger className="w-[160px]">
              <SelectValue placeholder="Intent" />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="all">All intents</SelectItem>
              {intents.map((s) => (
                <SelectItem key={s} value={s}>
                  {s}
                </SelectItem>
              ))}
              <SelectItem value="none">No intent</SelectItem>
            </SelectContent>
          </Select>
          <Select value={status} onValueChange={setStatus}>
            <SelectTrigger className="w-[160px]">
              <SelectValue placeholder="Status" />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="all">All statuses</SelectItem>
              <SelectItem value="answered">Answered</SelectItem>
              <SelectItem value="unanswered">Unanswered</SelectItem>
            </SelectContent>
          </Select>
          <Input
            placeholder="Filter question..."
            value={questionFilter}
            onChange={(e) => setQuestionFilter(e.target.value)}
            className="w-[200px]"
          />
          <Input
            placeholder="Filter answer..."
            value={answerFilter}
            onChange={(e) => setAnswerFilter(e.target.value)}
            className="w-[200px]"
          />


          <Popover>
            <PopoverTrigger asChild>
              <Button
                variant="outline"
                className={cn(
                  "w-[260px] justify-start text-left font-normal",
                  !dateRange.from && !dateRange.to && "text-muted-foreground"
                )}
              >
                <CalendarIcon className="mr-2 h-4 w-4" />
                {dateRange.from ? (
                  dateRange.to ? (
                    <>
                      {format(dateRange.from, "LLL dd, y")} -{" "}
                      {format(dateRange.to, "LLL dd, y")}
                    </>
                  ) : (
                    format(dateRange.from, "LLL dd, y")
                  )
                ) : (
                  <span>Pick a date range</span>
                )}
              </Button>
            </PopoverTrigger>
            <PopoverContent className="w-auto p-0" align="start">
              <Calendar
                initialFocus
                mode="range"
                defaultMonth={dateRange.from}
                selected={{
                  from: dateRange.from,
                  to: dateRange.to,
                }}
                onSelect={(range) =>
                  setDateRange({ from: range?.from, to: range?.to })
                }
                numberOfMonths={2}
                className="p-3 pointer-events-auto"
              />
            </PopoverContent>
          </Popover>

          {(dateRange.from ||
            dateRange.to ||
            source !== "all" ||
            intent !== "all" ||
            status !== "all" ||
            questionFilter ||
            answerFilter ||
            search) && (
            <Button
              variant="ghost"
              size="sm"
              className="gap-1 text-muted-foreground"
              onClick={() => {
                setSearch("");
                setQuestionFilter("");
                setAnswerFilter("");
                setSource("all");
                setIntent("all");
                setStatus("all");
                setDateRange({});
              }}
            >
              <X className="h-4 w-4" />
              Clear filters
            </Button>
          )}

        </div>

        {isLoading ? (
          <div className="flex items-center justify-center py-16">
            <Loader2 className="h-6 w-6 animate-spin text-primary" />
          </div>
        ) : (
          <div className="rounded-lg border overflow-hidden">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead className="w-[140px]">Date</TableHead>
                  <TableHead className="w-[110px]">Source</TableHead>
                  <TableHead className="w-[110px]">Intent</TableHead>
                  <TableHead>Question</TableHead>
                  <TableHead>Answer</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {filtered.length === 0 ? (
                  <TableRow>
                    <TableCell colSpan={5} className="text-center text-muted-foreground py-10">
                      No questions found
                    </TableCell>
                  </TableRow>
                ) : (
                  filtered.map((p) => (
                    <TableRow key={p.id} className="align-top">
                      <TableCell className="text-xs whitespace-nowrap">
                        {new Date(p.askedAt).toLocaleString("en-GB")}
                      </TableCell>
                      <TableCell>
                        <Badge variant="outline" className="text-xs">
                          {p.source}
                        </Badge>
                      </TableCell>
                      <TableCell>
                        {p.intent ? (
                          <Badge variant="secondary" className="text-xs">
                            {p.intent}
                          </Badge>
                        ) : (
                          <span className="text-xs text-muted-foreground">-</span>
                        )}
                      </TableCell>
                      <TableCell className="text-sm font-medium max-w-[280px] whitespace-pre-wrap">
                        {p.question}
                      </TableCell>
                      <TableCell className="text-sm text-muted-foreground max-w-[420px] whitespace-pre-wrap">
                        {p.answer || <span className="text-triplex-amber">No answer</span>}
                      </TableCell>
                    </TableRow>
                  ))
                )}
              </TableBody>
            </Table>
          </div>
        )}
      </main>
    </div>
  );
}
