import { Receipt } from "lucide-react";

export function Header() {
  return (
    <header className="sticky top-0 z-40 w-full border-b bg-background/95 backdrop-blur supports-[backdrop-filter]:bg-background/60">
      <div className="container flex h-16 items-center gap-4">
        <div className="flex items-center gap-3">
          <div className="w-10 h-10 rounded-xl bg-gradient-to-br from-primary to-triplex-teal-light flex items-center justify-center shadow-md">
            <Receipt className="h-5 w-5 text-primary-foreground" />
          </div>
          <div>
            <h1 className="text-xl font-bold tracking-tight">
              <span className="text-gradient-primary">Triplex</span>
            </h1>
            <p className="text-xs text-muted-foreground -mt-0.5">ניהול חשבוניות חכם</p>
          </div>
        </div>
      </div>
    </header>
  );
}
