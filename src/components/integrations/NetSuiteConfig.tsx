import { useState } from "react";
import { useForm } from "react-hook-form";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { apiService } from "@/lib/api-service";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import { Badge } from "@/components/ui/badge";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Separator } from "@/components/ui/separator";
import { Loader2, CheckCircle2, XCircle, TestTube2, Save, Eye, EyeOff } from "lucide-react";
import { useToast } from "@/hooks/use-toast";

interface NetSuiteFormData {
  accountId: string;
  consumerKey: string;
  consumerSecret: string;
  tokenKey: string;
  tokenSecret: string;
  amountThreshold: number;
  currency: string;
  isEnabled: boolean;
  autoSync: boolean;
  autoSyncIntervalMinutes: number;
}

interface TestResult {
  success: boolean;
  accountName?: string;
  accountId?: string;
  message?: string;
  error?: string;
  responseTimeMs: number;
}

export function NetSuiteConfig() {
  const { toast } = useToast();
  const queryClient = useQueryClient();
  const [showSecrets, setShowSecrets] = useState(false);
  const [testResult, setTestResult] = useState<TestResult | null>(null);

  const { data: config, isLoading } = useQuery({
    queryKey: ["netsuite-config"],
    queryFn: () => apiService.get<NetSuiteFormData>("/api/integrations/netsuite/config"),
  });

  const { register, handleSubmit, watch, setValue, formState: { isDirty } } = useForm<NetSuiteFormData>({
    values: config ?? {
      accountId: "",
      consumerKey: "",
      consumerSecret: "",
      tokenKey: "",
      tokenSecret: "",
      amountThreshold: 5000,
      currency: "ILS",
      isEnabled: false,
      autoSync: false,
      autoSyncIntervalMinutes: 60,
    },
  });

  const isEnabled = watch("isEnabled");
  const autoSync = watch("autoSync");

  const saveMutation = useMutation({
    mutationFn: (data: NetSuiteFormData) =>
      apiService.post("/api/integrations/netsuite/config", data),
    onSuccess: () => {
      toast({ title: "✅ הגדרות NetSuite נשמרו בהצלחה" });
      queryClient.invalidateQueries({ queryKey: ["netsuite-config"] });
      queryClient.invalidateQueries({ queryKey: ["integration-status"] });
    },
    onError: () => {
      toast({ title: "שגיאה בשמירת הגדרות", variant: "destructive" });
    },
  });

  const testMutation = useMutation({
    mutationFn: (data: NetSuiteFormData) =>
      apiService.post<TestResult>("/api/integrations/netsuite/test", data),
    onSuccess: (result) => {
      setTestResult(result);
    },
    onError: () => {
      setTestResult({ success: false, error: "שגיאת תקשורת", responseTimeMs: 0 });
    },
  });

  const onSave = handleSubmit((data) => saveMutation.mutate(data));
  const onTest = handleSubmit((data) => testMutation.mutate(data));

  if (isLoading) {
    return (
      <div className="flex justify-center py-12">
        <Loader2 className="h-6 w-6 animate-spin text-muted-foreground" />
      </div>
    );
  }

  return (
    <div className="space-y-6">
      {/* Connection Credentials */}
      <Card>
        <CardHeader>
          <div className="flex items-center justify-between">
            <div>
              <CardTitle>פרטי חיבור NetSuite</CardTitle>
              <CardDescription>
                Token-Based Authentication (TBA) — OAuth 1.0 HMAC-SHA256
              </CardDescription>
            </div>
            <Button
              variant="ghost"
              size="sm"
              onClick={() => setShowSecrets(!showSecrets)}
              className="gap-1.5"
            >
              {showSecrets ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
              {showSecrets ? "הסתר" : "הצג"} מפתחות
            </Button>
          </div>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
            <div className="space-y-1.5">
              <Label htmlFor="accountId">Account ID *</Label>
              <Input
                id="accountId"
                placeholder="לדוגמה: 1234567"
                {...register("accountId", { required: true })}
                dir="ltr"
              />
              <p className="text-xs text-muted-foreground">
                מזהה החשבון ב-NetSuite (ניתן למצוא בכתובת ה-URL)
              </p>
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="currency">מטבע</Label>
              <Input
                id="currency"
                placeholder="ILS"
                {...register("currency")}
                dir="ltr"
              />
            </div>
          </div>

          <Separator />

          <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
            <div className="space-y-1.5">
              <Label htmlFor="consumerKey">Consumer Key</Label>
              <Input
                id="consumerKey"
                type={showSecrets ? "text" : "password"}
                placeholder="Consumer Key מה-OAuth Application"
                {...register("consumerKey")}
                dir="ltr"
                autoComplete="off"
              />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="consumerSecret">Consumer Secret</Label>
              <Input
                id="consumerSecret"
                type={showSecrets ? "text" : "password"}
                placeholder="Consumer Secret"
                {...register("consumerSecret")}
                dir="ltr"
                autoComplete="off"
              />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="tokenKey">Token Key</Label>
              <Input
                id="tokenKey"
                type={showSecrets ? "text" : "password"}
                placeholder="Token Key מה-Access Token"
                {...register("tokenKey")}
                dir="ltr"
                autoComplete="off"
              />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="tokenSecret">Token Secret</Label>
              <Input
                id="tokenSecret"
                type={showSecrets ? "text" : "password"}
                placeholder="Token Secret"
                {...register("tokenSecret")}
                dir="ltr"
                autoComplete="off"
              />
            </div>
          </div>

          <div className="bg-muted/50 rounded-md p-3 text-xs text-muted-foreground space-y-1">
            <p className="font-medium">כיצד ליצור TBA credentials ב-NetSuite:</p>
            <p>1. Setup → Company → Enable Features → SuiteCloud → Token-Based Authentication ✓</p>
            <p>2. Setup → Integration → OAuth 2.0 Audit Log → New Integration</p>
            <p>3. Setup → Users/Roles → Access Tokens → New Access Token</p>
          </div>
        </CardContent>
      </Card>

      {/* Sync Settings */}
      <Card>
        <CardHeader>
          <CardTitle>הגדרות סנכרון</CardTitle>
          <CardDescription>קביעת סף הסכום ותדירות הסנכרון</CardDescription>
        </CardHeader>
        <CardContent className="space-y-5">
          <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
            <div className="space-y-1.5">
              <Label htmlFor="amountThreshold">סף סכום לבקשת הקצאה (₪)</Label>
              <Input
                id="amountThreshold"
                type="number"
                min={0}
                step={100}
                {...register("amountThreshold", { valueAsNumber: true })}
                dir="ltr"
              />
              <p className="text-xs text-muted-foreground">
                חשבוניות מעל סכום זה יישלחו לרשות המיסים לקבלת מספר הקצאה
              </p>
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="autoSyncInterval">תדירות סנכרון אוטומטי (דקות)</Label>
              <Input
                id="autoSyncInterval"
                type="number"
                min={15}
                step={15}
                {...register("autoSyncIntervalMinutes", { valueAsNumber: true })}
                dir="ltr"
                disabled={!autoSync}
              />
            </div>
          </div>

          <div className="space-y-4">
            <div className="flex items-center justify-between">
              <div>
                <p className="font-medium text-sm">הפעל אינטגרציה</p>
                <p className="text-xs text-muted-foreground">
                  אפשר סנכרון בין NetSuite לרשות המיסים
                </p>
              </div>
              <Switch
                checked={isEnabled}
                onCheckedChange={(v) => setValue("isEnabled", v, { shouldDirty: true })}
              />
            </div>

            <div className="flex items-center justify-between">
              <div>
                <p className="font-medium text-sm">סנכרון אוטומטי</p>
                <p className="text-xs text-muted-foreground">
                  הפעל סנכרון אוטומטי בהתאם לתדירות שנקבעה
                </p>
              </div>
              <Switch
                checked={autoSync}
                onCheckedChange={(v) => setValue("autoSync", v, { shouldDirty: true })}
                disabled={!isEnabled}
              />
            </div>
          </div>
        </CardContent>
      </Card>

      {/* Test Result */}
      {testResult && (
        <Alert variant={testResult.success ? "default" : "destructive"}>
          {testResult.success
            ? <CheckCircle2 className="h-4 w-4" />
            : <XCircle className="h-4 w-4" />}
          <AlertDescription>
            {testResult.success
              ? `✅ חיבור תקין — ${testResult.accountName ?? testResult.accountId} (${testResult.responseTimeMs}ms)`
              : `❌ ${testResult.error}`}
          </AlertDescription>
        </Alert>
      )}

      {/* Action Buttons */}
      <div className="flex gap-3 flex-wrap">
        <Button
          onClick={onSave}
          disabled={saveMutation.isPending}
          className="gap-1.5"
        >
          {saveMutation.isPending
            ? <Loader2 className="h-4 w-4 animate-spin" />
            : <Save className="h-4 w-4" />}
          שמור הגדרות
        </Button>
        <Button
          variant="outline"
          onClick={onTest}
          disabled={testMutation.isPending}
          className="gap-1.5"
        >
          {testMutation.isPending
            ? <Loader2 className="h-4 w-4 animate-spin" />
            : <TestTube2 className="h-4 w-4" />}
          בדוק חיבור
        </Button>
      </div>
    </div>
  );
}
