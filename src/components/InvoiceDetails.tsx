import { format } from "date-fns";
import { he } from "date-fns/locale";
import { X, Calendar, Receipt, FileText } from "lucide-react";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";

interface Invoice {
  id: string;
  invoice_number?: string;
  invoice_date?: string;
  total_amount?: number;
  currency?: string;
  image_url?: string;
  created_at: string;
}

interface InvoiceDetailsProps {
  invoice: Invoice;
  onClose: () => void;
}

export function InvoiceDetails({ invoice, onClose }: InvoiceDetailsProps) {
  const formatCurrency = (amount: number | undefined | null, currency?: string | null) => {
    if (amount === undefined || amount === null) return "—";
    return new Intl.NumberFormat("he-IL", {
      style: "currency",
      currency: currency || "ILS",
    }).format(amount);
  };

  const formatDate = (dateStr: string | undefined | null) => {
    if (!dateStr) return "—";
    try {
      return format(new Date(dateStr), "dd MMMM yyyy", { locale: he });
    } catch {
      return dateStr;
    }
  };

  return (
    <div className="fixed inset-0 z-50 bg-background/80 backdrop-blur-sm animate-fade-in">
      <div className="fixed inset-y-0 left-0 w-full max-w-md bg-background shadow-2xl animate-slide-in-right overflow-auto">
        <div className="sticky top-0 z-10 bg-background/95 backdrop-blur border-b px-6 py-4 flex items-center justify-between">
          <div className="flex items-center gap-3">
            <div className="w-10 h-10 rounded-lg bg-primary/10 flex items-center justify-center">
              <Receipt className="h-5 w-5 text-primary" />
            </div>
            <div>
              <h2 className="text-lg font-semibold">פרטי חשבונית</h2>
              <p className="text-sm text-muted-foreground">
                {invoice.invoice_number || "מספר לא זמין"}
              </p>
            </div>
          </div>
          <Button variant="ghost" size="icon" onClick={onClose}>
            <X className="h-5 w-5" />
          </Button>
        </div>

        <div className="p-6 space-y-6">
          {/* Invoice Image */}
          {invoice.image_url && (
            <Card>
              <CardHeader className="pb-3">
                <CardTitle className="text-base flex items-center gap-2">
                  <FileText className="h-4 w-4" />
                  תמונת החשבונית
                </CardTitle>
              </CardHeader>
              <CardContent>
                <a href={invoice.image_url} target="_blank" rel="noopener noreferrer">
                  <img
                    src={invoice.image_url}
                    alt="Invoice"
                    className="w-full max-h-64 object-contain rounded-lg border hover:opacity-90 transition-opacity cursor-pointer"
                  />
                </a>
              </CardContent>
            </Card>
          )}

          {/* Invoice Details */}
          <Card>
            <CardHeader className="pb-3">
              <CardTitle className="text-base flex items-center gap-2">
                <Calendar className="h-4 w-4" />
                פרטי החשבונית
              </CardTitle>
            </CardHeader>
            <CardContent className="space-y-4">
              <div className="flex justify-between items-center">
                <span className="text-muted-foreground">מספר חשבונית</span>
                <span className="font-medium">{invoice.invoice_number || "—"}</span>
              </div>
              <div className="flex justify-between items-center">
                <span className="text-muted-foreground">תאריך</span>
                <span className="font-medium">{formatDate(invoice.invoice_date)}</span>
              </div>
              <div className="flex justify-between items-center">
                <span className="text-muted-foreground">מטבע</span>
                <span className="font-medium">{invoice.currency || "ILS"}</span>
              </div>
            </CardContent>
          </Card>

          {/* Total */}
          <Card className="bg-primary/5 border-primary/20">
            <CardContent className="pt-6">
              <div className="flex justify-between items-center">
                <span className="text-lg font-medium">סכום</span>
                <span className="text-2xl font-bold text-primary">
                  {formatCurrency(invoice.total_amount, invoice.currency)}
                </span>
              </div>
            </CardContent>
          </Card>
        </div>
      </div>
    </div>
  );
}
