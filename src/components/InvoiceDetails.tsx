import { useState } from "react";
import { format, isValid, parse } from "date-fns";
import { X, Calendar, Receipt, FileText, Pencil, Check, Loader2, AlertCircle, CheckCircle2 } from "lucide-react";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { supabase } from "@/integrations/supabase/client";
import { toast } from "sonner";

interface Invoice {
  id: string;
  invoice_number?: string;
  invoice_date?: string;
  total_amount?: number;
  subtotal?: number;
  currency?: string;
  image_url?: string;
  created_at: string;
}

interface InvoiceDetailsProps {
  invoice: Invoice;
  onClose: () => void;
  onUpdate?: (updatedInvoice: Invoice) => void;
}

interface ValidationResult {
  isValid: boolean;
  message: string;
}

const validateAmount = (amount: number): ValidationResult => {
  if (isNaN(amount)) {
    return { isValid: false, message: "Amount must be a number" };
  }
  if (amount < 0) {
    return { isValid: false, message: "Amount must be positive" };
  }
  if (amount > 10000000) {
    return { isValid: false, message: "Amount too high (max 10,000,000)" };
  }
  return { isValid: true, message: "Amount valid ✓" };
};

const validateDate = (dateStr: string): ValidationResult => {
  if (!dateStr) {
    return { isValid: false, message: "Date is required" };
  }
  
  const parsedDate = parse(dateStr, "yyyy-MM-dd", new Date());
  
  if (!isValid(parsedDate)) {
    return { isValid: false, message: "Invalid date format" };
  }
  
  const today = new Date();
  const minDate = new Date("1990-01-01");
  
  if (parsedDate > today) {
    return { isValid: false, message: "Date cannot be in the future" };
  }
  
  if (parsedDate < minDate) {
    return { isValid: false, message: "Date too early (minimum 1990)" };
  }
  
  return { isValid: true, message: "Date valid ✓" };
};

export function InvoiceDetails({ invoice, onClose, onUpdate }: InvoiceDetailsProps) {
  const [isEditing, setIsEditing] = useState(false);
  const [isSaving, setIsSaving] = useState(false);
  const [editData, setEditData] = useState({
    invoice_number: invoice.invoice_number || "",
    invoice_date: invoice.invoice_date || "",
    total_amount: invoice.total_amount ?? invoice.subtotal ?? 0,
    currency: invoice.currency || "USD",
  });

  const amountValidation = validateAmount(editData.total_amount);
  const dateValidation = validateDate(editData.invoice_date);
  const isFormValid = amountValidation.isValid && dateValidation.isValid;

  const amount = invoice.total_amount ?? invoice.subtotal;
  
  const formatCurrency = (amount: number | undefined | null, currency?: string | null) => {
    if (amount === undefined || amount === null) return "—";
    return new Intl.NumberFormat("en-US", {
      style: "currency",
      currency: currency || "USD",
    }).format(amount);
  };

  const formatDate = (dateStr: string | undefined | null) => {
    if (!dateStr) return "—";
    try {
      return format(new Date(dateStr), "dd MMMM yyyy");
    } catch {
      return dateStr;
    }
  };

  const handleSave = async () => {
    if (!isFormValid) {
      if (!amountValidation.isValid) {
        toast.error(`Amount error: ${amountValidation.message}`);
      }
      if (!dateValidation.isValid) {
        toast.error(`Date error: ${dateValidation.message}`);
      }
      return;
    }

    setIsSaving(true);
    try {
      const { error } = await supabase
        .from("invoices")
        .update({
          invoice_number: editData.invoice_number || null,
          invoice_date: editData.invoice_date || null,
          total_amount: editData.total_amount || null,
          currency: editData.currency || "USD",
        })
        .eq("id", invoice.id);

      if (error) throw error;

      // Save corrections for AI learning
      const { data: { user } } = await supabase.auth.getUser();
      if (user) {
        const corrections: Array<{ user_id: string; field_name: string; original_value: string; corrected_value: string; context?: string }> = [];
        const context = invoice.invoice_number || undefined;

        if (editData.total_amount !== (invoice.total_amount ?? invoice.subtotal ?? 0)) {
          corrections.push({ user_id: user.id, field_name: "total_amount", original_value: String(invoice.total_amount ?? ""), corrected_value: String(editData.total_amount), context });
        }
        if (editData.invoice_number !== (invoice.invoice_number || "")) {
          corrections.push({ user_id: user.id, field_name: "invoice_number", original_value: invoice.invoice_number || "", corrected_value: editData.invoice_number, context });
        }
        if (editData.currency !== (invoice.currency || "USD")) {
          corrections.push({ user_id: user.id, field_name: "currency", original_value: invoice.currency || "USD", corrected_value: editData.currency, context });
        }
        if (editData.invoice_date !== (invoice.invoice_date || "")) {
          corrections.push({ user_id: user.id, field_name: "invoice_date", original_value: invoice.invoice_date || "", corrected_value: editData.invoice_date, context });
        }

        if (corrections.length > 0) {
          await supabase.from("invoice_corrections").insert(corrections);
          console.log(`Saved ${corrections.length} correction(s) for AI learning`);
        }
      }

      const updatedInvoice = {
        ...invoice,
        invoice_number: editData.invoice_number || undefined,
        invoice_date: editData.invoice_date || undefined,
        total_amount: editData.total_amount || undefined,
        currency: editData.currency || "USD",
      };

      onUpdate?.(updatedInvoice);
      setIsEditing(false);
      toast.success("Invoice updated successfully");
    } catch (error) {
      console.error("Error updating invoice:", error);
      toast.error("Error updating invoice");
    } finally {
      setIsSaving(false);
    }
  };

  const handleCancel = () => {
    setEditData({
      invoice_number: invoice.invoice_number || "",
      invoice_date: invoice.invoice_date || "",
      total_amount: invoice.total_amount ?? invoice.subtotal ?? 0,
      currency: invoice.currency || "USD",
    });
    setIsEditing(false);
  };

  const ValidationIndicator = ({ validation }: { validation: ValidationResult }) => (
    <div className={`flex items-center gap-1 text-xs mt-1 ${validation.isValid ? "text-green-600" : "text-destructive"}`}>
      {validation.isValid ? (
        <CheckCircle2 className="h-3 w-3" />
      ) : (
        <AlertCircle className="h-3 w-3" />
      )}
      <span>{validation.message}</span>
    </div>
  );

  return (
    <div className="fixed inset-0 z-50 bg-background/80 backdrop-blur-sm animate-fade-in">
      <div className="fixed inset-y-0 right-0 w-full max-w-md bg-background shadow-2xl animate-slide-in-right overflow-auto">
        <div className="sticky top-0 z-10 bg-background/95 backdrop-blur border-b px-6 py-4 flex items-center justify-between">
          <div className="flex items-center gap-3">
            <div className="w-10 h-10 rounded-lg bg-primary/10 flex items-center justify-center">
              <Receipt className="h-5 w-5 text-primary" />
            </div>
            <div>
              <h2 className="text-lg font-semibold">Invoice Details</h2>
              <p className="text-sm text-muted-foreground">
                {invoice.invoice_number || "Number unavailable"}
              </p>
            </div>
          </div>
          <div className="flex items-center gap-2">
            {!isEditing && (
              <Button variant="outline" size="icon" onClick={() => setIsEditing(true)}>
                <Pencil className="h-4 w-4" />
              </Button>
            )}
            <Button variant="ghost" size="icon" onClick={onClose}>
              <X className="h-5 w-5" />
            </Button>
          </div>
        </div>

        <div className="p-6 space-y-6">
          {invoice.image_url && (
            <Card>
              <CardHeader className="pb-3">
                <CardTitle className="text-base flex items-center gap-2">
                  <FileText className="h-4 w-4" />
                  Invoice Image
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

          <Card>
            <CardHeader className="pb-3">
              <CardTitle className="text-base flex items-center gap-2">
                <Calendar className="h-4 w-4" />
                Invoice Information
              </CardTitle>
            </CardHeader>
            <CardContent className="space-y-4">
              {isEditing ? (
                <>
                  <div className="space-y-2">
                    <Label htmlFor="invoice_number">Invoice Number</Label>
                    <Input
                      id="invoice_number"
                      value={editData.invoice_number}
                      onChange={(e) => setEditData({ ...editData, invoice_number: e.target.value })}
                      placeholder="Enter invoice number"
                    />
                  </div>
                  <div className="space-y-2">
                    <Label htmlFor="invoice_date">Date</Label>
                    <Input
                      id="invoice_date"
                      type="date"
                      value={editData.invoice_date}
                      onChange={(e) => setEditData({ ...editData, invoice_date: e.target.value })}
                      className={!dateValidation.isValid ? "border-destructive" : ""}
                    />
                    <ValidationIndicator validation={dateValidation} />
                  </div>
                  <div className="space-y-2">
                    <Label htmlFor="currency">Currency</Label>
                    <Select
                      value={editData.currency}
                      onValueChange={(value) => setEditData({ ...editData, currency: value })}
                    >
                      <SelectTrigger id="currency">
                        <SelectValue placeholder="Select currency" />
                      </SelectTrigger>
                      <SelectContent>
                        <SelectItem value="ILS">₪ ILS</SelectItem>
                        <SelectItem value="USD">$ USD</SelectItem>
                        <SelectItem value="EUR">€ EUR</SelectItem>
                        <SelectItem value="GBP">£ GBP</SelectItem>
                        <SelectItem value="PHP">₱ PHP</SelectItem>
                      </SelectContent>
                    </Select>
                  </div>
                  <div className="space-y-2">
                    <Label htmlFor="total_amount">Amount</Label>
                    <Input
                      id="total_amount"
                      type="number"
                      step="0.01"
                      min="0"
                      value={editData.total_amount}
                      onChange={(e) => setEditData({ ...editData, total_amount: parseFloat(e.target.value) || 0 })}
                      className={!amountValidation.isValid ? "border-destructive" : ""}
                    />
                    <ValidationIndicator validation={amountValidation} />
                  </div>
                  <div className="flex gap-2 pt-2">
                    <Button onClick={handleSave} disabled={isSaving || !isFormValid} className="flex-1 gap-2">
                      {isSaving ? (
                        <Loader2 className="h-4 w-4 animate-spin" />
                      ) : (
                        <Check className="h-4 w-4" />
                      )}
                      Save
                    </Button>
                    <Button variant="outline" onClick={handleCancel} disabled={isSaving} className="flex-1">
                      Cancel
                    </Button>
                  </div>
                </>
              ) : (
                <>
                  <div className="flex justify-between items-center">
                    <span className="text-muted-foreground">Invoice Number</span>
                    <span className="font-medium">{invoice.invoice_number || "—"}</span>
                  </div>
                  <div className="flex justify-between items-center">
                    <span className="text-muted-foreground">Date</span>
                    <span className="font-medium">{formatDate(invoice.invoice_date)}</span>
                  </div>
                  <div className="flex justify-between items-center">
                    <span className="text-muted-foreground">Currency</span>
                    <span className="font-medium">{invoice.currency || "USD"}</span>
                  </div>
                </>
              )}
            </CardContent>
          </Card>

          {!isEditing && (
            <Card className="bg-primary/5 border-primary/20">
              <CardContent className="pt-6">
                <div className="flex justify-between items-center">
                  <span className="text-lg font-medium">Amount</span>
                  <span className="text-2xl font-bold text-primary">
                    {formatCurrency(amount, invoice.currency)}
                  </span>
                </div>
              </CardContent>
            </Card>
          )}
        </div>
      </div>
    </div>
  );
}
