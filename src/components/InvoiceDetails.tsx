import { format } from "date-fns";
import { he } from "date-fns/locale";
import { X, Building2, User, Calendar, Receipt, FileText, Download } from "lucide-react";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Separator } from "@/components/ui/separator";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";

interface LineItem {
  description: string;
  quantity: number;
  unit_price: number;
  total: number;
}

interface Invoice {
  id: string;
  invoice_number?: string;
  invoice_date?: string;
  due_date?: string;
  vendor_name?: string;
  vendor_address?: string;
  vendor_phone?: string;
  vendor_email?: string;
  vendor_id?: string;
  customer_name?: string;
  customer_address?: string;
  subtotal?: number;
  tax_amount?: number;
  total_amount?: number;
  currency?: string;
  line_items?: LineItem[];
  notes?: string;
  payment_terms?: string;
  image_url?: string;
  status?: string;
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

  const lineItems = (invoice.line_items as LineItem[] | undefined) || [];

  return (
    <div className="fixed inset-0 z-50 bg-background/80 backdrop-blur-sm animate-fade-in">
      <div className="fixed inset-y-0 left-0 w-full max-w-2xl bg-background shadow-2xl animate-slide-in-right overflow-auto">
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

          {/* Vendor Info */}
          <Card>
            <CardHeader className="pb-3">
              <CardTitle className="text-base flex items-center gap-2">
                <Building2 className="h-4 w-4" />
                פרטי ספק
              </CardTitle>
            </CardHeader>
            <CardContent className="grid gap-3">
              <div className="grid grid-cols-2 gap-4">
                <div>
                  <p className="text-sm text-muted-foreground">שם הספק</p>
                  <p className="font-medium">{invoice.vendor_name || "—"}</p>
                </div>
                <div>
                  <p className="text-sm text-muted-foreground">מספר עוסק</p>
                  <p className="font-medium">{invoice.vendor_id || "—"}</p>
                </div>
              </div>
              {invoice.vendor_address && (
                <div>
                  <p className="text-sm text-muted-foreground">כתובת</p>
                  <p className="font-medium">{invoice.vendor_address}</p>
                </div>
              )}
              <div className="grid grid-cols-2 gap-4">
                {invoice.vendor_phone && (
                  <div>
                    <p className="text-sm text-muted-foreground">טלפון</p>
                    <p className="font-medium">{invoice.vendor_phone}</p>
                  </div>
                )}
                {invoice.vendor_email && (
                  <div>
                    <p className="text-sm text-muted-foreground">אימייל</p>
                    <p className="font-medium">{invoice.vendor_email}</p>
                  </div>
                )}
              </div>
            </CardContent>
          </Card>

          {/* Customer Info */}
          {(invoice.customer_name || invoice.customer_address) && (
            <Card>
              <CardHeader className="pb-3">
                <CardTitle className="text-base flex items-center gap-2">
                  <User className="h-4 w-4" />
                  פרטי לקוח
                </CardTitle>
              </CardHeader>
              <CardContent className="grid gap-3">
                <div>
                  <p className="text-sm text-muted-foreground">שם</p>
                  <p className="font-medium">{invoice.customer_name || "—"}</p>
                </div>
                {invoice.customer_address && (
                  <div>
                    <p className="text-sm text-muted-foreground">כתובת</p>
                    <p className="font-medium">{invoice.customer_address}</p>
                  </div>
                )}
              </CardContent>
            </Card>
          )}

          {/* Dates */}
          <Card>
            <CardHeader className="pb-3">
              <CardTitle className="text-base flex items-center gap-2">
                <Calendar className="h-4 w-4" />
                תאריכים
              </CardTitle>
            </CardHeader>
            <CardContent>
              <div className="grid grid-cols-2 gap-4">
                <div>
                  <p className="text-sm text-muted-foreground">תאריך חשבונית</p>
                  <p className="font-medium">{formatDate(invoice.invoice_date)}</p>
                </div>
                <div>
                  <p className="text-sm text-muted-foreground">תאריך לתשלום</p>
                  <p className="font-medium">{formatDate(invoice.due_date)}</p>
                </div>
              </div>
            </CardContent>
          </Card>

          {/* Line Items */}
          {lineItems.length > 0 && (
            <Card>
              <CardHeader className="pb-3">
                <CardTitle className="text-base">פריטים</CardTitle>
              </CardHeader>
              <CardContent className="p-0">
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead className="text-right">תיאור</TableHead>
                      <TableHead className="text-center">כמות</TableHead>
                      <TableHead className="text-left">מחיר</TableHead>
                      <TableHead className="text-left">סה"כ</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {lineItems.map((item, index) => (
                      <TableRow key={index}>
                        <TableCell className="font-medium">{item.description}</TableCell>
                        <TableCell className="text-center">{item.quantity}</TableCell>
                        <TableCell>{formatCurrency(item.unit_price, invoice.currency)}</TableCell>
                        <TableCell>{formatCurrency(item.total, invoice.currency)}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </CardContent>
            </Card>
          )}

          {/* Totals */}
          <Card className="bg-primary/5 border-primary/20">
            <CardContent className="pt-6">
              <div className="space-y-2">
                {invoice.subtotal !== undefined && invoice.subtotal !== null && (
                  <div className="flex justify-between text-sm">
                    <span className="text-muted-foreground">סכום ביניים</span>
                    <span>{formatCurrency(invoice.subtotal, invoice.currency)}</span>
                  </div>
                )}
                {invoice.tax_amount !== undefined && invoice.tax_amount !== null && (
                  <div className="flex justify-between text-sm">
                    <span className="text-muted-foreground">מע"מ</span>
                    <span>{formatCurrency(invoice.tax_amount, invoice.currency)}</span>
                  </div>
                )}
                <Separator className="my-2" />
                <div className="flex justify-between text-lg font-semibold">
                  <span>סה"כ לתשלום</span>
                  <span className="text-primary">
                    {formatCurrency(invoice.total_amount, invoice.currency)}
                  </span>
                </div>
              </div>
            </CardContent>
          </Card>

          {/* Notes */}
          {(invoice.notes || invoice.payment_terms) && (
            <Card>
              <CardHeader className="pb-3">
                <CardTitle className="text-base">הערות נוספות</CardTitle>
              </CardHeader>
              <CardContent className="space-y-3">
                {invoice.payment_terms && (
                  <div>
                    <p className="text-sm text-muted-foreground">תנאי תשלום</p>
                    <p>{invoice.payment_terms}</p>
                  </div>
                )}
                {invoice.notes && (
                  <div>
                    <p className="text-sm text-muted-foreground">הערות</p>
                    <p>{invoice.notes}</p>
                  </div>
                )}
              </CardContent>
            </Card>
          )}
        </div>
      </div>
    </div>
  );
}
