import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { apiService } from "@/lib/api-service";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Switch } from "@/components/ui/switch";
import { Label } from "@/components/ui/label";
import { Input } from "@/components/ui/input";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Separator } from "@/components/ui/separator";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import {
  Collapsible,
  CollapsibleContent,
  CollapsibleTrigger,
} from "@/components/ui/collapsible";
import {
  Loader2,
  Play,
  FlaskConical,
  ChevronDown,
  ChevronRight,
  CheckCircle2,
  XCircle,
  Clock,
  AlertCircle,
} from "lucide-react";
import { useToast } from "@/hooks/use-toast";

interface SyncRequest {
  dryRun: boolean;
  dateFrom?: string;
  dateTo?: string;
  amountThresholdOverride?: number;
}

interface SyncDetail {
  netSuiteId: string;
  tranId: string;
  amount: number;
  vendorName?: string;
  status: string;
  allocationNumber?: string;
  message?: string;
}

interface SyncResult {
  syncId: string;
  success: boolean;
  isDryRun: boolean;
  totalExpensesFetched: number;
  expensesAboveThreshold: number;
  allocationRequested: number;
  allocationApproved: number;
  allocationFailed: number;
  alreadyProcessed: number;
  details: SyncDetail[];
  error?: string;
  startedAt: string;
  completedAt?: string;
  durationMs: number;
}

interface SyncHistoryEntry {
  id: string;
  status: string;
  isDryRun: boolean;
  totalFetched: number;
  aboveThreshold: number;
  approved: number;
  failed: number;
  errorMessage?: string;
  startedAt: string;
  completedAt?: string;
  durationMs: number;
}

export function SyncDashboard() {
  const { toast } = useToast();
  const queryClient = useQueryClient();
  const [dryRun, setDryRun] = useState(true);
  const [dateFrom, setDateFrom] = useState("");
  const [dateTo, setDateTo] = useState("");
  const [overrideThreshold, setOverrideThreshold] = useState("");
  const [lastResult, setLastResult] = useState<SyncResult | null>(null);
  const [expandedDetails, setExpandedDetails] = useState(false);

  const { data: history = [], isLoading: historyLoading } = useQuery({
    queryKey: ["sync-history"],
    queryFn: () => apiService.get<SyncHistoryEntry[]>("/api/integrations/sync/history?limit=20"),
    refetchInterval: 30_000,
  });

  const syncMutation = useMutation({
    mutationFn: (req: SyncRequest) =>
      apiService.post<SyncResult>("/api/integrations/sync", req),
    onSuccess: (result) => {
      setLastResult(result);
      queryClient.invalidateQueries({ queryKey: ["sync-history"] });
      queryClient.invalidateQueries({ queryKey: ["integration-status"] });
      const label = result.isDryRun ? "[DryRun] " : "";
      if (result.success) {
        toast({
          title: `${label}✅ סנכרון הושלם`,
          description: `אושרו: ${result.allocationApproved} | נכשלו: ${result.allocationFailed}`,
        });
      } else {
        toast({
          title: `${label}⚠️ סנכרון הסתיים עם שגיאות`,
          description: result.error ?? `נכשלו: ${result.allocationFailed}`,
          variant: "destructive",
        });
      }
    },
    onError: () => {
      toast({ title: "שגיאת תקשורת בסנכרון", variant: "destructive" });
    },
  });

  const handleSync = () => {
    syncMutation.mutate({
      dryRun,
      dateFrom: dateFrom || undefined,
      dateTo: dateTo || undefined,
      amountThresholdOverride: overrideThreshold ? Number(overrideThreshold) : undefined,
    });
  };

  return (
    <div className="space-y-6">
      {/* Sync Controls */}
      <Card>
        <CardHeader>
          <CardTitle>הפעלת סנכרון</CardTitle>
          <CardDescription>
            שליפת הוצאות מ-NetSuite ובקשת מספרי הקצאה מרשות המיסים
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-5">
          {/* Dry Run Toggle */}
          <div className="flex items-center justify-between p-3 bg-muted/50 rounded-lg">
            <div>
              <p className="font-medium text-sm">מצב בדיקה (Dry Run)</p>
              <p className="text-xs text-muted-foreground">
                מציג מה היה מתרחש — ללא שליחה לרשות המיסים
              </p>
            </div>
            <Switch checked={dryRun} onCheckedChange={setDryRun} />
          </div>

          {!dryRun && (
            <Alert>
              <AlertCircle className="h-4 w-4" />
              <AlertDescription className="text-sm">
                מצב ייצור — בקשות יישלחו לרשות המיסים. וודא שהגדרות ה-API נכונות.
              </AlertDescription>
            </Alert>
          )}

          {/* Date Range */}
          <div className="grid grid-cols-2 gap-4">
            <div className="space-y-1.5">
              <Label>מתאריך</Label>
              <Input
                type="date"
                value={dateFrom}
                onChange={(e) => setDateFrom(e.target.value)}
                dir="ltr"
              />
              <p className="text-xs text-muted-foreground">ריק = 30 ימים אחורה</p>
            </div>
            <div className="space-y-1.5">
              <Label>עד תאריך</Label>
              <Input
                type="date"
                value={dateTo}
                onChange={(e) => setDateTo(e.target.value)}
                dir="ltr"
              />
            </div>
          </div>

          {/* Override Threshold */}
          <div className="space-y-1.5">
            <Label>עקיפת סף סכום (אופציונלי)</Label>
            <Input
              type="number"
              value={overrideThreshold}
              onChange={(e) => setOverrideThreshold(e.target.value)}
              placeholder="ריק = שימוש בסף שהוגדר"
              dir="ltr"
            />
          </div>

          <div className="flex gap-3">
            <Button
              onClick={handleSync}
              disabled={syncMutation.isPending}
              className="gap-2"
              variant={dryRun ? "outline" : "default"}
            >
              {syncMutation.isPending ? (
                <Loader2 className="h-4 w-4 animate-spin" />
              ) : dryRun ? (
                <FlaskConical className="h-4 w-4" />
              ) : (
                <Play className="h-4 w-4" />
              )}
              {dryRun ? "הרץ סימולציה" : "הרץ סנכרון"}
            </Button>
          </div>
        </CardContent>
      </Card>

      {/* Last Sync Result */}
      {lastResult && (
        <Card>
          <CardHeader>
            <div className="flex items-center justify-between">
              <CardTitle className="text-base">
                תוצאת סנכרון אחרון
                {lastResult.isDryRun && (
                  <Badge variant="secondary" className="mr-2 text-xs">DryRun</Badge>
                )}
              </CardTitle>
              <span className="text-xs text-muted-foreground">
                {lastResult.durationMs}ms
              </span>
            </div>
          </CardHeader>
          <CardContent className="space-y-4">
            {/* Stats */}
            <div className="grid grid-cols-2 md:grid-cols-4 gap-3 text-center">
              <StatBox label="נשלפו" value={lastResult.totalExpensesFetched} />
              <StatBox label="מעל הסף" value={lastResult.expensesAboveThreshold} />
              <StatBox label="אושרו ✅" value={lastResult.allocationApproved} positive />
              <StatBox label="נכשלו ❌" value={lastResult.allocationFailed} negative />
            </div>

            {lastResult.error && (
              <Alert variant="destructive">
                <AlertDescription>{lastResult.error}</AlertDescription>
              </Alert>
            )}

            {/* Details Table */}
            {lastResult.details.length > 0 && (
              <Collapsible open={expandedDetails} onOpenChange={setExpandedDetails}>
                <CollapsibleTrigger asChild>
                  <Button variant="ghost" size="sm" className="w-full gap-1">
                    {expandedDetails ? <ChevronDown className="h-4 w-4" /> : <ChevronRight className="h-4 w-4" />}
                    פרטי הוצאות ({lastResult.details.length})
                  </Button>
                </CollapsibleTrigger>
                <CollapsibleContent>
                  <div className="mt-2 rounded-md border overflow-auto max-h-72">
                    <Table>
                      <TableHeader>
                        <TableRow>
                          <TableHead className="text-right">מספר עסקה</TableHead>
                          <TableHead className="text-right">ספק</TableHead>
                          <TableHead className="text-left">סכום</TableHead>
                          <TableHead className="text-right">סטטוס</TableHead>
                          <TableHead className="text-right">מספר הקצאה</TableHead>
                        </TableRow>
                      </TableHeader>
                      <TableBody>
                        {lastResult.details.map((d) => (
                          <TableRow key={d.netSuiteId}>
                            <TableCell className="font-mono text-xs">{d.tranId}</TableCell>
                            <TableCell className="text-xs">{d.vendorName ?? "—"}</TableCell>
                            <TableCell className="text-xs text-left" dir="ltr">
                              ₪{d.amount.toLocaleString("he-IL")}
                            </TableCell>
                            <TableCell>
                              <StatusBadge status={d.status} />
                            </TableCell>
                            <TableCell className="font-mono text-xs">
                              {d.allocationNumber ?? "—"}
                            </TableCell>
                          </TableRow>
                        ))}
                      </TableBody>
                    </Table>
                  </div>
                </CollapsibleContent>
              </Collapsible>
            )}
          </CardContent>
        </Card>
      )}

      {/* Sync History */}
      <Card>
        <CardHeader>
          <CardTitle>היסטוריית סנכרון</CardTitle>
          <CardDescription>20 הרצות אחרונות</CardDescription>
        </CardHeader>
        <CardContent>
          {historyLoading ? (
            <div className="flex justify-center py-8">
              <Loader2 className="h-5 w-5 animate-spin text-muted-foreground" />
            </div>
          ) : history.length === 0 ? (
            <p className="text-center text-muted-foreground py-8 text-sm">
              טרם בוצע סנכרון
            </p>
          ) : (
            <div className="rounded-md border overflow-auto">
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead className="text-right">תאריך</TableHead>
                    <TableHead className="text-right">סטטוס</TableHead>
                    <TableHead className="text-left">נשלפו</TableHead>
                    <TableHead className="text-left">אושרו</TableHead>
                    <TableHead className="text-left">נכשלו</TableHead>
                    <TableHead className="text-left">זמן</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {history.map((entry) => (
                    <TableRow key={entry.id}>
                      <TableCell className="text-xs">
                        {new Date(entry.startedAt).toLocaleString("he-IL")}
                        {entry.isDryRun && (
                          <Badge variant="outline" className="mr-1 text-[10px] h-4">DryRun</Badge>
                        )}
                      </TableCell>
                      <TableCell>
                        <HistoryStatusBadge status={entry.status} />
                      </TableCell>
                      <TableCell className="text-xs">{entry.totalFetched}</TableCell>
                      <TableCell className="text-xs text-green-600">{entry.approved}</TableCell>
                      <TableCell className="text-xs text-red-500">{entry.failed}</TableCell>
                      <TableCell className="text-xs text-muted-foreground">{entry.durationMs}ms</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </div>
          )}
        </CardContent>
      </Card>
    </div>
  );
}

// ─────────────────────────────────────────────────────────────────
// Sub-components
// ─────────────────────────────────────────────────────────────────

function StatBox({ label, value, positive, negative }: {
  label: string;
  value: number;
  positive?: boolean;
  negative?: boolean;
}) {
  return (
    <div className="bg-muted/50 rounded-lg p-3">
      <p className="text-xs text-muted-foreground">{label}</p>
      <p className={`text-2xl font-bold ${positive && value > 0 ? "text-green-600" : negative && value > 0 ? "text-red-500" : ""}`}>
        {value}
      </p>
    </div>
  );
}

function StatusBadge({ status }: { status: string }) {
  switch (status) {
    case "approved":
      return <Badge className="bg-green-600 text-[10px]">אושר ✓</Badge>;
    case "failed":
      return <Badge variant="destructive" className="text-[10px]">נכשל</Badge>;
    case "already_processed":
      return <Badge variant="secondary" className="text-[10px]">קיים</Badge>;
    case "skipped":
      return <Badge variant="outline" className="text-[10px]">דולג</Badge>;
    case "fetched":
      return <Badge variant="outline" className="text-[10px]">נשלף</Badge>;
    default:
      return <Badge variant="outline" className="text-[10px]">{status}</Badge>;
  }
}

function HistoryStatusBadge({ status }: { status: string }) {
  switch (status) {
    case "success":
      return <Badge className="bg-green-600 text-[10px]">הצלחה</Badge>;
    case "partial":
      return <Badge variant="secondary" className="text-[10px] bg-yellow-500 text-white">חלקי</Badge>;
    case "failed":
      return <Badge variant="destructive" className="text-[10px]">כישלון</Badge>;
    default:
      return <Badge variant="outline" className="text-[10px]">{status}</Badge>;
  }
}
