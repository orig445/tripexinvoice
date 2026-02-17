import { FileText, TrendingUp, Building2, Calendar } from "lucide-react";
import { Card, CardContent } from "@/components/ui/card";

interface DashboardStatsProps {
  invoices: any[];
}

export function DashboardStats({ invoices }: DashboardStatsProps) {
  const totalAmount = invoices.reduce((sum, inv) => sum + (inv.total_amount || 0), 0);
  const uniqueVendors = new Set(invoices.map((inv) => inv.vendor_name).filter(Boolean)).size;
  
  const thisMonth = new Date();
  thisMonth.setDate(1);
  const thisMonthInvoices = invoices.filter((inv) => {
    const date = new Date(inv.created_at);
    return date >= thisMonth;
  });

  const stats = [
    {
      label: "Total Invoices",
      value: invoices.length.toString(),
      icon: FileText,
      color: "text-primary",
      bgColor: "bg-primary/10",
    },
    {
      label: "Total Amount",
      value: new Intl.NumberFormat("en-US", {
        style: "currency",
        currency: "USD",
        maximumFractionDigits: 0,
      }).format(totalAmount),
      icon: TrendingUp,
      color: "text-triplex-success",
      bgColor: "bg-triplex-success/10",
    },
    {
      label: "Vendors",
      value: uniqueVendors.toString(),
      icon: Building2,
      color: "text-triplex-info",
      bgColor: "bg-triplex-info/10",
    },
    {
      label: "This Month",
      value: thisMonthInvoices.length.toString(),
      icon: Calendar,
      color: "text-triplex-amber",
      bgColor: "bg-triplex-amber/10",
    },
  ];

  return (
    <div className="grid grid-cols-2 lg:grid-cols-4 gap-4">
      {stats.map((stat, index) => (
        <Card key={stat.label} className="animate-fade-in" style={{ animationDelay: `${index * 100}ms` }}>
          <CardContent className="p-4 md:p-6">
            <div className="flex items-center gap-4">
              <div className={`w-12 h-12 rounded-xl ${stat.bgColor} flex items-center justify-center shrink-0`}>
                <stat.icon className={`h-6 w-6 ${stat.color}`} />
              </div>
              <div className="min-w-0">
                <p className="text-sm text-muted-foreground truncate">{stat.label}</p>
                <p className="text-2xl font-bold truncate">{stat.value}</p>
              </div>
            </div>
          </CardContent>
        </Card>
      ))}
    </div>
  );
}
