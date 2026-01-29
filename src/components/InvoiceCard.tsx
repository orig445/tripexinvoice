import { format } from "date-fns";
import { he } from "date-fns/locale";
import { FileText, Calendar, Eye, Trash2 } from "lucide-react";
import { Card, CardContent, CardHeader } from "@/components/ui/card";
import { Button } from "@/components/ui/button";

interface InvoiceCardProps {
  invoice: {
    id: string;
    invoice_number?: string;
    invoice_date?: string;
    total_amount?: number;
    subtotal?: number;
    currency?: string;
    image_url?: string;
    created_at: string;
  };
  onView: (invoice: any) => void;
  onDelete: (id: string) => void;
}

export function InvoiceCard({ invoice, onView, onDelete }: InvoiceCardProps) {
  // Use total_amount, fallback to subtotal if not available
  const amount = invoice.total_amount ?? invoice.subtotal;

  const formatCurrency = (amount: number | undefined, currency?: string | null) => {
    if (!amount) return "—";
    return new Intl.NumberFormat("he-IL", {
      style: "currency",
      currency: currency || "ILS",
    }).format(amount);
  };

  const formatDate = (dateStr: string | undefined) => {
    if (!dateStr) return "—";
    try {
      return format(new Date(dateStr), "dd/MM/yyyy", { locale: he });
    } catch {
      return dateStr;
    }
  };

  return (
    <Card className="group hover:shadow-lg transition-all duration-300 hover:-translate-y-1 animate-fade-in overflow-hidden">
      <CardHeader className="pb-3">
        <div className="flex items-center gap-3">
          <div className="w-10 h-10 rounded-lg bg-primary/10 flex items-center justify-center shrink-0">
            <FileText className="h-5 w-5 text-primary" />
          </div>
          <div className="min-w-0 flex-1">
            <h3 className="font-semibold text-foreground truncate">
              {invoice.invoice_number || "ללא מספר"}
            </h3>
          </div>
        </div>
      </CardHeader>
      
      <CardContent className="pt-0">
        <div className="space-y-3 mb-4">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-2 text-sm text-muted-foreground">
              <Calendar className="h-4 w-4" />
              <span>תאריך</span>
            </div>
            <span className="font-medium">{formatDate(invoice.invoice_date)}</span>
          </div>
          <div className="flex items-center justify-between">
            <span className="text-sm text-muted-foreground">סכום</span>
            <span className="font-semibold text-lg text-primary">
              {formatCurrency(amount, invoice.currency)}
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
