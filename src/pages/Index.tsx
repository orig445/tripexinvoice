import { useState, useEffect } from "react";
import { FileText, Upload } from "lucide-react";
import { Header } from "@/components/Header";
import { InvoiceUploader } from "@/components/InvoiceUploader";
import { InvoiceList } from "@/components/InvoiceList";
import { InvoiceDetails } from "@/components/InvoiceDetails";
import { DashboardStats } from "@/components/DashboardStats";
import { supabase } from "@/integrations/supabase/client";
import { toast } from "sonner";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { ChatbotWidget } from "@/components/chatbot/ChatbotWidget";

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

  const handleInvoiceUpdate = (updatedInvoice: any) => {
    setInvoices((prev) =>
      prev.map((inv) => (inv.id === updatedInvoice.id ? updatedInvoice : inv))
    );
    setSelectedInvoice(updatedInvoice);
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
            ) : (
              <InvoiceList
                invoices={invoices}
                onView={setSelectedInvoice}
                onDelete={handleDeleteInvoice}
              />
            )}
          </TabsContent>
        </Tabs>
      </main>

      {/* Invoice Details Drawer */}
      {selectedInvoice && (
        <InvoiceDetails
          invoice={selectedInvoice}
          onClose={() => setSelectedInvoice(null)}
          onUpdate={handleInvoiceUpdate}
        />
      )}

      <ChatbotWidget />
    </div>
  );
};

export default Index;
