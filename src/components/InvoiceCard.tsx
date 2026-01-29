import { format } from "date-fns";
import { he } from "date-fns/locale";
import { FileText, Building2, Calendar, DollarSign, Eye, Trash2 } from "lucide-react";
import { Card, CardContent, CardHeader } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";

interface InvoiceCardProps {
  invoice: {
    id: string;
    invoice_number?: string;
    invoice_date?: string;
    vendor_name?: string;
    total_amount?: number;
    currency?: string;
    status?: string;
    image_url?: string;
    created_at: string;
  };
  onView: (invoice: any) => void;
  onDelete: (id: string) => void;
}

export function InvoiceCard({ invoice, onView, onDelete }: InvoiceCardProps) {
  const formatCurrency = (amount: number | undefined, currency: string = "ILS") => {
    if (!amount) return "—";
    return new Intl.NumberFormat("he-IL", {
      style: "currency",
      currency,
    }).format(amount);
  };

  const formatDate = (dateStr: string | undefined) => {
    if (!dateStr) return "—";
    try {
      return format(new Date(dateStr), "dd MMM yyyy", { locale: he });
    } catch {
      return dateStr;
    }
  };

  return (
    <Card className="group hover:shadow-lg transition-all duration-300 hover:-translate-y-1 animate-fade-in overflow-hidden">
      <CardHeader className="pb-3">
        <div className="flex items-start justify-between gap-4">
          <div className="flex items-center gap-3">
            <div className="w-12 h-12 rounded-xl bg-primary/10 flex items-center justify-center shrink-0">
              <FileText className="h-6 w-6 text-primary" />
            </div>
            <div className="min-w-0">
              <h3 className="font-semibold text-foreground truncate">
                {invoice.invoice_number || "חשבונית"}
              </h3>
              <div className="flex items-center gap-1.5 text-sm text-muted-foreground">
                <Building2 className="h-3.5 w-3.5" />
                <span className="truncate">{invoice.vendor_name || "ספק לא ידוע"}</span>
              </div>
            </div>
          </div>
          <Badge
            variant="secondary"
            className={cn(
              "shrink-0",
              invoice.status === "processed" && "bg-triplex-success/10 text-triplex-success border-triplex-success/20"
            )}
          >
            {invoice.status === "processed" ? "מעובד" : invoice.status}
          </Badge>
        </div>
      </CardHeader>
      
      <CardContent className="pt-0">
        <div className="grid grid-cols-2 gap-4 mb-4">
          <div className="flex items-center gap-2 text-sm">
            <Calendar className="h-4 w-4 text-muted-foreground" />
            <span className="text-muted-foreground">תאריך:</span>
            <span className="font-medium">{formatDate(invoice.invoice_date)}</span>
          </div>
          <div className="flex items-center gap-2 text-sm">
            <DollarSign className="h-4 w-4 text-muted-foreground" />
            <span className="text-muted-foreground">סכום:</span>
            <span className="font-semibold text-primary">
              {formatCurrency(invoice.total_amount, invoice.currency)}
            </span>
          </div>
        </div>

        <div className="flex gap-2 pt-3 border-t border-border">
          <Button
            variant="outline"
            size="sm"
            className="flex-1 gap-2"
            onClick={() => onView(invoice)}
          >
            <Eye className="h-4 w-4" />
            צפייה
          </Button>
          <Button
            variant="ghost"
            size="sm"
            className="text-destructive hover:text-destructive hover:bg-destructive/10"
            onClick={() => onDelete(invoice.id)}
          >
            <Trash2 className="h-4 w-4" />
          </Button>
        </div>
      </CardContent>
    </Card>
  );
}
