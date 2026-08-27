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
import { format, isWithinInterval, startOfDay, endOfDay } from "date-fns";
import { cn } from "@/lib/utils";

interface QaPair {
  id: string;
  sessionId: string;
  source: string;
  intent: string | null;
  question: string;
  answer: string;
  askedAt: string;
  answeredAt: string | null;
}

const MESSAGE_LIMIT = 2000;

export default function QaDashboard() {
  const [pairs, setPairs] = useState<QaPair[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [search, setSearch] = useState("");
  const [source, setSource] = useState("all");
  const [intent, setIntent] = useState("all");
  const [dateRange, setDateRange] = useState<{ from?: Date; to?: Date }>({});

  const load = async () => {
    setIsLoading(true);

    const [{ data: messages }, { data: sessions }] = await Promise.all([
      supabase
        .from("chat_messages")
        .select("id, session_id, role, content, intent, created_at")
        .order("created_at", { ascending: true })
        .limit(MESSAGE_LIMIT),
      supabase.from("chat_sessions").select("id, source"),
    ]);

    const sourceById = new Map<string, string>(
      (sessions || []).map((s: any) => [s.id, s.source || "web"])
    );

    const bySession = new Map<string, any[]>();
    (messages || []).forEach((m: any) => {
      const list = bySession.get(m.session_id) || [];
      list.push(m);
      bySession.set(m.session_id, list);
    });

    const result: QaPair[] = [];
    bySession.forEach((list, sessionId) => {
      list.forEach((msg, i) => {
        if (msg.role !== "user") return;
        const reply = list.slice(i + 1).find((m) => m.role !== "user");
        result.push({
          id: msg.id,
          sessionId,
          source: sourceById.get(sessionId) || "web",
          intent: reply?.intent || msg.intent || null,
          question: msg.content,
          answer: reply?.content || "",
          askedAt: msg.created_at,
          answeredAt: reply?.created_at || null,
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

  const filtered = useMemo(() => {
    const q = search.trim().toLowerCase();
    return pairs.filter((p) => {
      if (source !== "all" && p.source !== source) return false;
      if (intent !== "all" && p.intent !== intent) return false;
      if (dateRange.from || dateRange.to) {
        const asked = new Date(p.askedAt);
        const from = dateRange.from ? startOfDay(dateRange.from) : undefined;
        const to = dateRange.to ? endOfDay(dateRange.to) : undefined;
        if (!isWithinInterval(asked, { start: from || asked, end: to || asked })) {
          return false;
        }
      }
      if (!q) return true;
      return (
        p.question.toLowerCase().includes(q) || p.answer.toLowerCase().includes(q)
      );
    });
  }, [pairs, search, source, intent, dateRange]);

  const unanswered = filtered.filter((p) => !p.answer).length;
  const sessionCount = new Set(filtered.map((p) => p.sessionId)).size;

  const exportCsv = () => {
    const esc = (v: string) => `"${(v || "").replace(/"/g, '""')}"`;
    const rows = [
      ["Date", "Source", "Intent", "Question", "Answer"].join(","),
      ...filtered.map((p) =>
        [
          esc(new Date(p.askedAt).toISOString()),
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
            </SelectContent>
          </Select>

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
              />
            </PopoverContent>
          </Popover>

          {(dateRange.from || dateRange.to || source !== "all" || intent !== "all" || search) && (
            <Button
              variant="ghost"
              size="sm"
              className="gap-1 text-muted-foreground"
              onClick={() => {
                setSearch("");
                setSource("all");
                setIntent("all");
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
