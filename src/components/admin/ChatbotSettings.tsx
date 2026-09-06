import { useState, useEffect } from "react";
import { supabase } from "@/integrations/supabase/client";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";
import { Switch } from "@/components/ui/switch";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Save, Loader2 } from "lucide-react";

export function ChatbotSettings() {
  const [config, setConfig] = useState<any>(null);
  const [isSaving, setIsSaving] = useState(false);
  const [isLoading, setIsLoading] = useState(true);

  useEffect(() => {
    loadConfig();
  }, []);

  const loadConfig = async () => {
    const { data } = await supabase.from("chatbot_config").select("*").limit(1).single();
    if (data) setConfig(data);
    setIsLoading(false);
  };

  const handleSave = async () => {
    if (!config) return;
    setIsSaving(true);
    const { data, error } = await supabase
      .from("chatbot_config")
      .update({
        bot_name: config.bot_name,
        welcome_message: config.welcome_message,
        system_prompt: config.system_prompt,
        model_name: config.model_name,
        max_tokens: config.max_tokens,
        temperature: config.temperature,
        is_active: config.is_active,
      })
      .eq("id", config.id)
      .select();

    if (error) {
      console.error("Save config error:", error);
      toast.error("Failed to save settings: " + error.message);
    } else if (!data || data.length === 0) {
      console.error("No rows updated - likely RLS blocking. User may not have admin role.");
      toast.error("You do not have permission to update settings. Admin role required.");
    } else {
      toast.success("Settings saved successfully");
    }
    setIsSaving(false);
  };

  if (isLoading) {
    return (
      <div className="flex items-center justify-center py-12">
        <Loader2 className="h-6 w-6 animate-spin text-primary" />
      </div>
    );
  }

  if (!config) return <p className="text-muted-foreground">No settings found</p>;

  return (
    <div className="space-y-6">
      <Card>
        <CardHeader>
          <CardTitle className="text-lg">General settings</CardTitle>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="grid gap-4 sm:grid-cols-2">
            <div className="space-y-2">
              <Label>Bot name</Label>
              <Input
                value={config.bot_name}
                onChange={(e) => setConfig({ ...config, bot_name: e.target.value })}
              />
            </div>
            <div className="flex items-center gap-3 pt-6">
              <Switch
                checked={config.is_active}
                onCheckedChange={(checked) => setConfig({ ...config, is_active: checked })}
              />
              <Label>Bot active</Label>
            </div>
          </div>
          <div className="space-y-2">
            <Label>Welcome message</Label>
            <Textarea
              value={config.welcome_message}
              onChange={(e) => setConfig({ ...config, welcome_message: e.target.value })}
              rows={2}
            />
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle className="text-lg">System Prompt</CardTitle>
        </CardHeader>
        <CardContent>
          <Textarea
            value={config.system_prompt}
            onChange={(e) => setConfig({ ...config, system_prompt: e.target.value })}
            rows={12}
            className="font-mono text-xs"
          />
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle className="text-lg">Model settings</CardTitle>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="space-y-2">
            <Label>Model name</Label>
            <Input
              value={config.model_name}
              onChange={(e) => setConfig({ ...config, model_name: e.target.value })}
            />
          </div>
          <div className="grid gap-4 sm:grid-cols-2">
            <div className="space-y-2">
              <Label>Max Tokens</Label>
              <Input
                type="number"
                value={config.max_tokens}
                onChange={(e) => setConfig({ ...config, max_tokens: parseInt(e.target.value) || 1024 })}
              />
            </div>
            <div className="space-y-2">
              <Label>Temperature ({config.temperature})</Label>
              <Input
                type="number"
                step="0.1"
                min="0"
                max="2"
                value={config.temperature}
                onChange={(e) => setConfig({ ...config, temperature: parseFloat(e.target.value) || 0.3 })}
              />
            </div>
          </div>
        </CardContent>
      </Card>

      <Button onClick={handleSave} disabled={isSaving} className="gap-2">
        {isSaving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
        Save settings
      </Button>
    </div>
  );
}
