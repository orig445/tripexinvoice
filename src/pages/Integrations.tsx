import { Header } from "@/components/Header";
import { NetSuiteConfig } from "@/components/integrations/NetSuiteConfig";
import { TaxAuthorityConfig } from "@/components/integrations/TaxAuthorityConfig";
import { SyncDashboard } from "@/components/integrations/SyncDashboard";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Settings2, Building2, RefreshCw, Activity } from "lucide-react";
import { Card, CardContent } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { useQuery } from "@tanstack/react-query";
import { apiService } from "@/lib/api-service";

const Integrations = () => {
  const { data: status } = useQuery({
    queryKey: ["integration-status"],
    queryFn: () => apiService.get<IntegrationStatus>("/api/integrations/status"),
    refetchInterval: 60_000,
  });

  return (
    <div className="min-h-screen bg-background" dir="rtl">
      <Header />
      <main className="container py-6 md:py-10 space-y-6">
        {/* Page Title */}
        <div className="flex items-start justify-between">
          <div>
            <h1 className="text-2xl font-bold">אינטגרציות חיצוניות</h1>
            <p className="text-muted-foreground mt-1">
              חיבור NetSuite לרשות המיסים — אוטומציה לבקשת מספרי הקצאה
            </p>
          </div>
          <div className="flex gap-2 flex-wrap justify-end">
            <StatusBadge
              label="NetSuite"
              configured={status?.netSuiteConfigured}
              enabled={status?.netSuiteEnabled}
            />
            <StatusBadge
              label="רשות המיסים"
              configured={status?.taxAuthorityConfigured}
              enabled={status?.taxAuthorityEnabled}
            />
          </div>
        </div>

        {/* Summary Cards */}
        {status && (
          <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
            <SummaryCard
              title="סף סכום"
              value={`₪${status.amountThreshold?.toLocaleString("he-IL") ?? "5,000"}`}
              subtitle="לבקשת הקצאה"
              icon="₪"
            />
            <SummaryCard
              title="ממתינים להקצאה"
              value={String(status.pendingAllocations ?? 0)}
              subtitle="חשבוניות"
              icon="⏳"
            />
            <SummaryCard
              title="סנכרון אחרון"
              value={status.lastSyncAt
                ? new Date(status.lastSyncAt).toLocaleDateString("he-IL")
                : "טרם בוצע"}
              subtitle={status.lastSyncStatus ?? ""}
              icon="🔄"
            />
            <SummaryCard
              title="סטטוס כללי"
              value={status.netSuiteEnabled && status.taxAuthorityEnabled ? "פעיל" : "לא פעיל"}
              subtitle={status.netSuiteEnabled && status.taxAuthorityEnabled
                ? "כל האינטגרציות פעילות"
                : "דרוש הגדרה"}
              icon={status.netSuiteEnabled && status.taxAuthorityEnabled ? "✅" : "⚠️"}
            />
          </div>
        )}

        {/* Tabs */}
        <Tabs defaultValue="sync" className="space-y-6">
          <TabsList className="grid w-full max-w-xl grid-cols-3">
            <TabsTrigger value="sync" className="gap-1.5">
              <RefreshCw className="h-4 w-4" />
              סנכרון
            </TabsTrigger>
            <TabsTrigger value="netsuite" className="gap-1.5">
              <Settings2 className="h-4 w-4" />
              NetSuite
            </TabsTrigger>
            <TabsTrigger value="taxauthority" className="gap-1.5">
              <Building2 className="h-4 w-4" />
              רשות המיסים
            </TabsTrigger>
          </TabsList>

          <TabsContent value="sync">
            <SyncDashboard />
          </TabsContent>

          <TabsContent value="netsuite">
            <NetSuiteConfig />
          </TabsContent>

          <TabsContent value="taxauthority">
            <TaxAuthorityConfig />
          </TabsContent>
        </Tabs>
      </main>
    </div>
  );
};

// ─────────────────────────────────────────────────────────────────
// Sub-components
// ─────────────────────────────────────────────────────────────────

interface IntegrationStatus {
  netSuiteConfigured: boolean;
  netSuiteEnabled: boolean;
  taxAuthorityConfigured: boolean;
  taxAuthorityEnabled: boolean;
  lastSyncAt?: string;
  lastSyncStatus?: string;
  pendingAllocations: number;
  amountThreshold: number;
}

function StatusBadge({
  label,
  configured,
  enabled,
}: {
  label: string;
  configured?: boolean;
  enabled?: boolean;
}) {
  if (!configured) {
    return <Badge variant="outline" className="text-muted-foreground">{label}: לא מוגדר</Badge>;
  }
  if (!enabled) {
    return <Badge variant="secondary">{label}: מושבת</Badge>;
  }
  return <Badge className="bg-green-600 hover:bg-green-700">{label}: פעיל ✓</Badge>;
}

function SummaryCard({
  title,
  value,
  subtitle,
  icon,
}: {
  title: string;
  value: string;
  subtitle: string;
  icon: string;
}) {
  return (
    <Card>
      <CardContent className="pt-4 pb-3">
        <div className="flex items-start gap-2">
          <span className="text-xl">{icon}</span>
          <div className="min-w-0">
            <p className="text-xs text-muted-foreground truncate">{title}</p>
            <p className="text-lg font-bold truncate">{value}</p>
            {subtitle && (
              <p className="text-xs text-muted-foreground truncate">{subtitle}</p>
            )}
          </div>
        </div>
      </CardContent>
    </Card>
  );
}

export default Integrations;
