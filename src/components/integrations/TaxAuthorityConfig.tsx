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
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Separator } from "@/components/ui/separator";
import { Loader2, Save, Eye, EyeOff, Info, Construction } from "lucide-react";
import { useToast } from "@/hooks/use-toast";

interface TaxAuthorityFormData {
  apiBaseUrl: string;
  clientId: string;
  clientSecret: string;
  username: string;
  businessId: string;
  isEnabled: boolean;
  useSandbox: boolean;
}

export function TaxAuthorityConfig() {
  const { toast } = useToast();
  const queryClient = useQueryClient();
  const [showSecrets, setShowSecrets] = useState(false);

  const { data: config, isLoading } = useQuery({
    queryKey: ["taxauthority-config"],
    queryFn: () => apiService.get<TaxAuthorityFormData>("/api/integrations/taxauthority/config"),
  });

  const { register, handleSubmit, watch, setValue } = useForm<TaxAuthorityFormData>({
    values: config ?? {
      apiBaseUrl: "https://openapi.taxes.gov.il",
      clientId: "",
      clientSecret: "",
      username: "",
      businessId: "",
      isEnabled: false,
      useSandbox: true,
    },
  });

  const isEnabled = watch("isEnabled");
  const useSandbox = watch("useSandbox");

  const saveMutation = useMutation({
    mutationFn: (data: TaxAuthorityFormData) =>
      apiService.post("/api/integrations/taxauthority/config", data),
    onSuccess: () => {
      toast({ title: "✅ הגדרות רשות המיסים נשמרו" });
      queryClient.invalidateQueries({ queryKey: ["taxauthority-config"] });
      queryClient.invalidateQueries({ queryKey: ["integration-status"] });
    },
    onError: () => {
      toast({ title: "שגיאה בשמירת הגדרות", variant: "destructive" });
    },
  });

  const onSave = handleSubmit((data) => saveMutation.mutate(data));

  if (isLoading) {
    return (
      <div className="flex justify-center py-12">
        <Loader2 className="h-6 w-6 animate-spin text-muted-foreground" />
      </div>
    );
  }

  return (
    <div className="space-y-6">
      {/* Status Banner */}
      <Alert>
        <Construction className="h-4 w-4" />
        <AlertTitle>בסיס קיים — בהמשך פיתוח</AlertTitle>
        <AlertDescription>
          מבנה האינטגרציה עם רשות המיסים בנוי ומוכן. ניתן להגדיר את פרטי הגישה כעת,
          ה-API הסופי יחובר בשלב הבא. כל ההגדרות יישמרו ויהיו זמינות לשימוש.
        </AlertDescription>
      </Alert>

      {/* About Section */}
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Info className="h-5 w-5 text-blue-500" />
            מספר הקצאה — רשות המיסים
          </CardTitle>
        </CardHeader>
        <CardContent className="space-y-3 text-sm text-muted-foreground">
          <p>
            <strong className="text-foreground">מה זה מספר הקצאה?</strong> על-פי תיקון לחוק מע"מ,
            חשבוניות מס מעל סכום מסוים (כפי שנקבע בהגדרות NetSuite) חייבות לקבל אישור מרשות
            המיסים בצורת "מספר הקצאה" לפני שניתן לנכות מס תשומות.
          </p>
          <p>
            <strong className="text-foreground">כיצד עובד התהליך?</strong>
          </p>
          <ol className="list-decimal list-inside space-y-1 pr-2">
            <li>המערכת שולפת חשבוניות מ-NetSuite שמעל הסף</li>
            <li>לכל חשבונית — נשלחת בקשה לרשות המיסים</li>
            <li>רשות המיסים מחזירה מספר הקצאה ייחודי</li>
            <li>המספר נשמר ב-NetSuite ובמסד הנתונים</li>
          </ol>
          <div className="bg-muted rounded-md p-3 space-y-1">
            <p className="font-medium text-foreground text-xs">קישורים שימושיים:</p>
            <p className="text-xs">
              • API רשות המיסים:{" "}
              <a
                href="https://openapi.taxes.gov.il"
                target="_blank"
                rel="noopener noreferrer"
                className="text-blue-500 hover:underline"
                dir="ltr"
              >
                openapi.taxes.gov.il
              </a>
            </p>
            <p className="text-xs">
              • תיעוד מספר הקצאה:{" "}
              <a
                href="https://www.gov.il/he/departments/general/mas-hashlama"
                target="_blank"
                rel="noopener noreferrer"
                className="text-blue-500 hover:underline"
              >
                gov.il — מס הכנסה
              </a>
            </p>
          </div>
        </CardContent>
      </Card>

      {/* Connection Config */}
      <Card>
        <CardHeader>
          <div className="flex items-center justify-between">
            <div>
              <CardTitle>פרטי גישה ל-API</CardTitle>
              <CardDescription>
                OAuth 2.0 Client Credentials — רשות המיסים הישראלית
              </CardDescription>
            </div>
            <Button
              variant="ghost"
              size="sm"
              onClick={() => setShowSecrets(!showSecrets)}
              className="gap-1.5"
            >
              {showSecrets ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
              {showSecrets ? "הסתר" : "הצג"}
            </Button>
          </div>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
            <div className="space-y-1.5 md:col-span-2">
              <Label htmlFor="apiBaseUrl">כתובת ה-API</Label>
              <Input
                id="apiBaseUrl"
                {...register("apiBaseUrl")}
                dir="ltr"
                placeholder="https://openapi.taxes.gov.il"
              />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="businessId">ח.פ. / ע.מ. העסק *</Label>
              <Input
                id="businessId"
                {...register("businessId")}
                placeholder="מספר העוסק המורשה"
                dir="ltr"
              />
              <p className="text-xs text-muted-foreground">
                מספר זיהוי העסק ברשות המיסים
              </p>
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="username">שם משתמש (אופציונלי)</Label>
              <Input
                id="username"
                {...register("username")}
                placeholder="שם משתמש אם נדרש"
                dir="ltr"
              />
            </div>
          </div>

          <Separator />

          <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
            <div className="space-y-1.5">
              <Label htmlFor="clientId">Client ID</Label>
              <Input
                id="clientId"
                type={showSecrets ? "text" : "password"}
                {...register("clientId")}
                placeholder="Client ID מה-API"
                dir="ltr"
                autoComplete="off"
              />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="clientSecret">Client Secret</Label>
              <Input
                id="clientSecret"
                type={showSecrets ? "text" : "password"}
                {...register("clientSecret")}
                placeholder="Client Secret"
                dir="ltr"
                autoComplete="off"
              />
            </div>
          </div>

          <div className="space-y-4">
            <div className="flex items-center justify-between">
              <div>
                <p className="font-medium text-sm">סביבת Sandbox</p>
                <p className="text-xs text-muted-foreground">
                  השתמש בסביבת הבדיקות של רשות המיסים (מומלץ לבדיקות)
                </p>
              </div>
              <Switch
                checked={useSandbox}
                onCheckedChange={(v) => setValue("useSandbox", v)}
              />
            </div>

            {!useSandbox && (
              <Alert variant="destructive">
                <AlertDescription className="text-xs">
                  ⚠️ אתה עובד בסביבת ייצור! בקשות יישלחו לרשות המיסים האמיתית.
                </AlertDescription>
              </Alert>
            )}

            <div className="flex items-center justify-between">
              <div>
                <p className="font-medium text-sm">הפעל אינטגרציה</p>
                <p className="text-xs text-muted-foreground">
                  אפשר שליחת בקשות הקצאה לרשות המיסים
                </p>
              </div>
              <Switch
                checked={isEnabled}
                onCheckedChange={(v) => setValue("isEnabled", v)}
              />
            </div>
          </div>
        </CardContent>
      </Card>

      <div className="flex gap-3">
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
      </div>
    </div>
  );
}
