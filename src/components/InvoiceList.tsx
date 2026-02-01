import { useState, useMemo } from "react";
import { format, parseISO } from "date-fns";
import { he } from "date-fns/locale";
import { ChevronDown, ChevronLeft, Folder, FolderOpen, FileText } from "lucide-react";
import { InvoiceCard } from "./InvoiceCard";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";

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

interface InvoiceListProps {
  invoices: Invoice[];
  onView: (invoice: Invoice) => void;
  onDelete: (id: string) => void;
}

interface GroupedInvoices {
  [year: string]: {
    [month: string]: Invoice[];
  };
}

export function InvoiceList({ invoices, onView, onDelete }: InvoiceListProps) {
  const [expandedYears, setExpandedYears] = useState<Set<string>>(new Set());
  const [expandedMonths, setExpandedMonths] = useState<Set<string>>(new Set());

  // Group invoices by year and month based on invoice_date
  const groupedInvoices = useMemo(() => {
    const groups: GroupedInvoices = {};
    
    invoices.forEach((invoice) => {
      // Use invoice_date if available, otherwise fall back to created_at
      const dateStr = invoice.invoice_date || invoice.created_at;
      let date: Date;
      
      try {
        date = parseISO(dateStr);
      } catch {
        date = new Date(dateStr);
      }
      
      const year = format(date, "yyyy");
      const month = format(date, "MM");
      
      if (!groups[year]) {
        groups[year] = {};
      }
      if (!groups[year][month]) {
        groups[year][month] = [];
      }
      groups[year][month].push(invoice);
    });

    return groups;
  }, [invoices]);

  // Get sorted years (descending - newest first)
  const sortedYears = useMemo(() => {
    return Object.keys(groupedInvoices).sort((a, b) => parseInt(b) - parseInt(a));
  }, [groupedInvoices]);

  // Auto-expand current year and month on mount
  useMemo(() => {
    if (sortedYears.length > 0 && expandedYears.size === 0) {
      const currentYear = sortedYears[0];
      setExpandedYears(new Set([currentYear]));
      
      const months = Object.keys(groupedInvoices[currentYear] || {});
      if (months.length > 0) {
        const latestMonth = months.sort((a, b) => parseInt(b) - parseInt(a))[0];
        setExpandedMonths(new Set([`${currentYear}-${latestMonth}`]));
      }
    }
  }, [sortedYears]);

  const toggleYear = (year: string) => {
    const newExpanded = new Set(expandedYears);
    if (newExpanded.has(year)) {
      newExpanded.delete(year);
    } else {
      newExpanded.add(year);
    }
    setExpandedYears(newExpanded);
  };

  const toggleMonth = (yearMonth: string) => {
    const newExpanded = new Set(expandedMonths);
    if (newExpanded.has(yearMonth)) {
      newExpanded.delete(yearMonth);
    } else {
      newExpanded.add(yearMonth);
    }
    setExpandedMonths(newExpanded);
  };

  const getMonthName = (month: string) => {
    const date = new Date(2024, parseInt(month) - 1, 1);
    return format(date, "MMMM", { locale: he });
  };

  // Group totals by currency for a year
  const getYearTotalsByCurrency = (year: string) => {
    const totals: Record<string, number> = {};
    const yearData = groupedInvoices[year];
    Object.values(yearData).forEach((monthInvoices) => {
      monthInvoices.forEach((inv) => {
        const currency = inv.currency || "ILS";
        const amount = inv.total_amount ?? inv.subtotal ?? 0;
        totals[currency] = (totals[currency] || 0) + amount;
      });
    });
    return totals;
  };

  // Group totals by currency for a month
  const getMonthTotalsByCurrency = (year: string, month: string) => {
    const totals: Record<string, number> = {};
    groupedInvoices[year][month].forEach((inv) => {
      const currency = inv.currency || "ILS";
      const amount = inv.total_amount ?? inv.subtotal ?? 0;
      totals[currency] = (totals[currency] || 0) + amount;
    });
    return totals;
  };

  const formatCurrencyAmount = (amount: number, currency: string) => {
    return new Intl.NumberFormat("he-IL", {
      style: "currency",
      currency: currency,
      maximumFractionDigits: 0,
    }).format(amount);
  };

  const formatTotalsByCurrency = (totals: Record<string, number>) => {
    return Object.entries(totals)
      .map(([currency, amount]) => formatCurrencyAmount(amount, currency))
      .join(" | ");
  };

  if (invoices.length === 0) {
    return (
      <div className="text-center py-12">
        <div className="w-20 h-20 mx-auto mb-4 rounded-2xl bg-muted flex items-center justify-center">
          <FileText className="h-10 w-10 text-muted-foreground" />
        </div>
        <h3 className="text-lg font-medium mb-2">אין חשבוניות עדיין</h3>
        <p className="text-muted-foreground">העלה חשבונית ראשונה כדי להתחיל</p>
      </div>
    );
  }

  return (
    <div className="space-y-3">
      {sortedYears.map((year) => {
        const isYearExpanded = expandedYears.has(year);
        const yearInvoiceCount = Object.values(groupedInvoices[year]).flat().length;
        const sortedMonths = Object.keys(groupedInvoices[year]).sort(
          (a, b) => parseInt(b) - parseInt(a)
        );

        return (
          <div key={year} className="border rounded-xl overflow-hidden bg-card">
            {/* Year Header */}
            <Button
              variant="ghost"
              className="w-full justify-between px-4 py-4 h-auto hover:bg-accent/50"
              onClick={() => toggleYear(year)}
            >
              <div className="flex items-center gap-3">
                {isYearExpanded ? (
                  <ChevronDown className="h-5 w-5 text-muted-foreground" />
                ) : (
                  <ChevronLeft className="h-5 w-5 text-muted-foreground" />
                )}
                {isYearExpanded ? (
                  <FolderOpen className="h-5 w-5 text-primary" />
                ) : (
                  <Folder className="h-5 w-5 text-primary" />
                )}
                <span className="text-lg font-semibold">{year}</span>
                <Badge variant="secondary" className="mr-2">
                  {yearInvoiceCount} חשבוניות
                </Badge>
              </div>
              <span className="text-sm font-medium text-muted-foreground">
                {formatTotalsByCurrency(getYearTotalsByCurrency(year))}
              </span>
            </Button>

            {/* Months */}
            {isYearExpanded && (
              <div className="border-t">
                {sortedMonths.map((month) => {
                  const yearMonth = `${year}-${month}`;
                  const isMonthExpanded = expandedMonths.has(yearMonth);
                  const monthInvoices = groupedInvoices[year][month];

                  return (
                    <div key={month} className="border-b last:border-b-0">
                      {/* Month Header */}
                      <Button
                        variant="ghost"
                        className="w-full justify-between px-6 py-3 h-auto hover:bg-accent/30"
                        onClick={() => toggleMonth(yearMonth)}
                      >
                        <div className="flex items-center gap-3">
                          {isMonthExpanded ? (
                            <ChevronDown className="h-4 w-4 text-muted-foreground" />
                          ) : (
                            <ChevronLeft className="h-4 w-4 text-muted-foreground" />
                          )}
                          {isMonthExpanded ? (
                            <FolderOpen className="h-4 w-4 text-triplex-amber" />
                          ) : (
                            <Folder className="h-4 w-4 text-triplex-amber" />
                          )}
                          <span className="font-medium">{getMonthName(month)}</span>
                          <Badge variant="outline" className="mr-2">
                            {monthInvoices.length}
                          </Badge>
                        </div>
                        <span className="text-sm text-muted-foreground">
                          {formatTotalsByCurrency(getMonthTotalsByCurrency(year, month))}
                        </span>
                      </Button>

                      {/* Invoices Grid */}
                      {isMonthExpanded && (
                        <div className="p-4 bg-muted/30">
                          <div className="grid sm:grid-cols-2 lg:grid-cols-3 gap-4">
                            {monthInvoices.map((invoice) => (
                              <InvoiceCard
                                key={invoice.id}
                                invoice={invoice}
                                onView={onView}
                                onDelete={onDelete}
                              />
                            ))}
                          </div>
                        </div>
                      )}
                    </div>
                  );
                })}
              </div>
            )}
          </div>
        );
      })}
    </div>
  );
}
