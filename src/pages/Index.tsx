import { useState, useEffect } from "react";
import { FileText, Upload } from "lucide-react";
import { Header } from "@/components/Header";
import { InvoiceUploader } from "@/components/InvoiceUploader";
import { InvoiceCard } from "@/components/InvoiceCard";
import { InvoiceDetails } from "@/components/InvoiceDetails";
import { DashboardStats } from "@/components/DashboardStats";
import { supabase } from "@/integrations/supabase/client";
import { toast } from "sonner";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";

const Index = () => {
  const [invoices, setInvoices] = useState<any[]>([]);
  const [selectedInvoice, setSelectedInvoice] = useState<any | null>(null);
  const [isLoading, setIsLoading] = useState(true);

  const fetchInvoices = async () => {
    try {
      const { data, error } = await supabase
        .from("invoices")
        .select("*")
        .order("created_at", { ascending: false });

      if (error) throw error;
      setInvoices(data || []);
    } catch (error) {
      console.error("Error fetching invoices:", error);
      toast.error("שגיאה בטעינת החשבוניות");
    } finally {
      setIsLoading(false);
    }
  };

  useEffect(() => {
    fetchInvoices();
  }, []);

  const handleInvoiceProcessed = (invoice: any) => {
    setInvoices((prev) => [invoice, ...prev]);
  };

  const handleDeleteInvoice = async (id: string) => {
    try {
      const { error } = await supabase.from("invoices").delete().eq("id", id);
      if (error) throw error;
      setInvoices((prev) => prev.filter((inv) => inv.id !== id));
      toast.success("החשבונית נמחקה");
    } catch (error) {
      console.error("Error deleting invoice:", error);
      toast.error("שגיאה במחיקת החשבונית");
    }
  };

  return (
    <div className="min-h-screen bg-background" dir="rtl">
      <Header />

      <main className="container py-6 md:py-10 space-y-8">
        {/* Stats */}
        <DashboardStats invoices={invoices} />

        {/* Main Content */}
        <Tabs defaultValue="upload" className="space-y-6">
          <TabsList className="grid w-full max-w-md grid-cols-2 mx-auto">
            <TabsTrigger value="upload" className="gap-2">
              <Upload className="h-4 w-4" />
              העלאה חדשה
            </TabsTrigger>
            <TabsTrigger value="list" className="gap-2">
              <FileText className="h-4 w-4" />
              רשימת חשבוניות
            </TabsTrigger>
          </TabsList>

          <TabsContent value="upload" className="animate-fade-in">
            <div className="max-w-2xl mx-auto">
              <InvoiceUploader onInvoiceProcessed={handleInvoiceProcessed} />
            </div>
          </TabsContent>

          <TabsContent value="list" className="animate-fade-in">
            {isLoading ? (
              <div className="flex items-center justify-center py-12">
                <div className="w-8 h-8 border-4 border-primary border-t-transparent rounded-full animate-spin" />
              </div>
            ) : invoices.length === 0 ? (
              <div className="text-center py-12">
                <div className="w-20 h-20 mx-auto mb-4 rounded-2xl bg-muted flex items-center justify-center">
                  <FileText className="h-10 w-10 text-muted-foreground" />
                </div>
                <h3 className="text-lg font-medium mb-2">אין חשבוניות עדיין</h3>
                <p className="text-muted-foreground">העלה חשבונית ראשונה כדי להתחיל</p>
              </div>
            ) : (
              <div className="grid sm:grid-cols-2 lg:grid-cols-3 gap-4">
                {invoices.map((invoice) => (
                  <InvoiceCard
                    key={invoice.id}
                    invoice={invoice}
                    onView={setSelectedInvoice}
                    onDelete={handleDeleteInvoice}
                  />
                ))}
              </div>
            )}
          </TabsContent>
        </Tabs>
      </main>

      {/* Invoice Details Drawer */}
      {selectedInvoice && (
        <InvoiceDetails
          invoice={selectedInvoice}
          onClose={() => setSelectedInvoice(null)}
        />
      )}
    </div>
  );
};

export default Index;
