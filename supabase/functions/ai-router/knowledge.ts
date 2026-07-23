// ─────────────────────────────────────────────────────────────────────────────
// Built-in domain knowledge for TripEX AI (Milo).
//
// This is the assistant's own "brain" about the ComBTAS / TAS travel & expense
// system. It is ALWAYS injected into the system prompt so Milo can answer the
// user's specific case directly — without telling the user to go read an
// external user guide, chapter, or page number.
//
// TAS_REPORTS_KNOWLEDGE is sourced from the official "Analyst Report TAS"
// catalog (report codes, names and explanations). When a user asks which
// report shows a certain kind of data, Milo should name the exact report
// (code + name) and explain what it contains — in plain language.
// ─────────────────────────────────────────────────────────────────────────────

export const TAS_SYSTEM_KNOWLEDGE = `## Built-in System Knowledge (TAS / ComBTAS Travel & Expense)

Use this knowledge to answer the user's SPECIFIC situation directly. Explain what
is happening and exactly what they should do inside the TripEX/TAS system.

### Travel (Trip) statuses & the flow
A trip moves through these stages in order:
- **Draft** — the trip request was started but not yet sent. It is still editable and does NOT appear in most reports.
- **Sent for approval / Approval** — the trip request was submitted and is waiting for the manager/approver to approve it. It is locked for the traveler while it waits.
- **Approved** — the manager approved the travel request; the trip can now be handled/booked.
- **Issued / Ticketed** — flights, hotels or cars were actually booked (ticketed) by the agent or self-booking.
- **Open / Closed** — invoice/expense states after travel: "Open" = still being processed, "Closed" = fully reconciled.
- **Cancelled** — the trip or a service was cancelled.

### Expense report ("End Trip" / Expense Report) flow
1. The traveler finishes the trip and fills the **"End Trip Confirmation"** page (this is also where vacation/special days during the trip are declared).
2. The traveler **sends the expense report for approval**.
3. The report becomes **"Expense Approved"** once the approver signs it. Only "Expense Approved" data feeds the finance/expense reports.

### Why "I can't submit an expense report while my trip is in 'Approval' status"
An expense report is submitted at the END of the trip flow, not during it. If the
travel request itself is still in **Approval** status, it means the manager has not
yet approved the trip, so the trip has not been approved → issued → travelled, and
therefore there is nothing to close with an expense report yet. What to do:
- Wait for (or follow up with) the approving manager to approve the travel request.
- Once the trip is Approved and the travel actually happens (services Issued), go to
  the **End Trip Confirmation** page, then send the expense report for approval.
- If the trip is already over but still stuck in "Approval", the approver still needs
  to act on it — the expense report unlocks only after the travel request is approved.

### Statuses that appear in reports
Most reports exclude **Draft** and **Cancelled** trips. Report data is based on
Approved / Issued / Closed items and on Approved expense reports.`;

export const TAS_REPORTS_KNOWLEDGE = `## Built-in Reports Catalog (TAS Analyst Reports)

When a user asks about reports — which report to use, where to see certain data,
or what a report shows — answer using this catalog. Give the exact **report code
and name** and a short plain-language explanation. Do NOT tell the user to look it
up in an external guide.

### Finance › Accrual
- **7002 — Accrual**: Manages the company's accrual (provisions) report; built per company regulations.

### Finance › Budget
- **8001 — Budget Matrix**: Budget structure per cost center — planned travels plus actual travels.
- **8002 — Budget by Division**: How much of the budget each division/department/cost center has already used.
- **8003 — Budget by Company**: Budget already used at company level, including approved travels.
- **8004 — Budget by Division 2**: Budget usage broken down by different areas/regions.
- **8005 — Budget By Travel**: Budget exploitation sorted by project, budget section, employee, trip type — includes approved and not-yet-signed travels.

### Finance › General
- **6001 — Travel Days for Insurance**: Total travel days (status Approved→Closed) per travel, with traveler ID, birthday, company, units, destination and health declaration — everything an insurance company asks for.
- **10002 — Special Days Report**: Trips where the traveler declared vacation days during the trip (declared on the "End Trip Confirmation" page), for trips whose expense report was approved.

### Finance › Invoice
- **1014 — Saving**: The highest, lowest and chosen agent/self-booking proposals and the difference (savings) between them.
- **7001 — Invoice List**: All statement/invoice details — status (approved/open/closed), supplier, invoice no., coordinator, service, travel details. Filter by departure/issue/due/invoice date. Filtered by the traveler's company.
- **7010 — Invoice List Interface Data**: Invoice list including the interface-transfer data — invoice status vs. the interface (Open/Approved/Closed), file-send date, and batch number.
- **7011 — Invoice List by Service**: Invoices split by flights, hotels, cars and other — deeper analysis via many parameters. Filtered by the trip's project company.
- **7012 — Issued Services With/Without Invoice**: All issued services (flights/hotels/cars) by trip no., showing which agent invoices arrived or are missing. Filter by supplier, invoice state and trip status.

### Finance › Redundancy (per-diem / tax redundancy)
- **7003 — Redundancy by Travel**: Approved statements & expenses vs. the system's redundancy tables (Israeli tax law), showing amount in $, permitted amount and redundancy amount — filtered by travel.
- **7004 — Redundancy by Month**: Same redundancy calculation, filtered by month.
- **7005 — Redundancy By Year**: Same redundancy calculation, filtered by year.

### Management › Expense
- **7006 — Expense Report by Type**: Only "Expense Approved" expenses, derived from statements + the traveler's expense report — with TAS no., agency, invoice no., dates, destination, units, project, employee, total.

### Travel Reports › Air (flights)
- **1005 — Volume by Agent**: Volume per agent by service (flight/hotel/car/other) for issued travels; cost per project/budget item. Table + graph.
- **2001 — Airline Volume**: Flight volume by airline for issued (ticketed) flights only; excludes cancelled.
- **2002 — Volume By Destination**: Flight volume by destination (continent, city) for issued travels; excludes cancelled.
- **2003 — Agreement Targets**: Airline agreements vs. actual issued flights (agreements are entered by the customer).
- **2004 — Airline Class of Booking**: Reservations by airline, ticket no., flight class, ticket price plus AP taxes.
- **2007 — Airline Class of Booking by Date Sent**: Class-of-booking view by the date the booking was sent.
- **2005 — Volume By Airline and Destination**: How many flights per destination by each airline (issued status), with company, unit, dates, employee, project.
- **2006 — UATP / AirPlus Tickets**: All flights paid with a UATP/AirPlus card, with details and total cost.
- **2008 — Flight Change Tracking**: Tracks flight changes.
- **2009 — Volume By Agency and Airline**: Volume broken down by agency and airline.
- **2010 — Airline Tickets**: Number of tickets per airline; shows the highest flight class on the ticket (BUSINESS if any leg is business; ECONOMY if no class match is found).

### Travel Reports › Hotel
- **3001 — Hotels By Agency and Destination**: Hotel details for issued travels by destination and agent.
- **3002 — Hotels By Destination (detailed)**: Accommodation costs by destination/country/city — employee, cost center, hotel, nights, average cost per day, % of total accommodation cost.

### Travel Reports › Car
- **4001 — Cars By Rental Company and Destination (detailed)**: Issued car rentals by rental company; excludes cancelled/draft.
- **4002 — Cars By Agency and Destination**: Chosen & issued car quotes by destination city (a quote with no destination city, or in cancelled/draft, is excluded).

### Travel Reports › Expense
- **1002 — Travel Expenses**: All approved expenses filtered by worker/unit/project/expense type — with travel no., unit, name, dates, expense type, form of payment, original amount, amount in USD, project.
- **1004 — Top Spender**: The biggest spenders in the organization (approved expenses + issued), filterable by traveler name or expense type.
- **1015 — Travel Management Control**: All non-draft, non-cancelled trips from the travel request; shows ACCEPTANCE DATE and ISSUE DATE and the gap between them; filter by coordinator. Does not include out-of-trip expense reports.
- **7012 — Travel Expenses Tracking**: Trip expenses by expense type for the trips in the system.

### Travel Reports › General
- **1001 — Travel by Categories**: Volume of the travel budget by expense category (flight, car, hotel, hospitality, internet, …) from employee expenses and issued quotes; can compare periods.
- **1003 — Advance Purchase Summary**: By how many days each trip was pre-ordered (reservation-to-departure); excludes cancelled/draft.
- **1006 — Travel Status**: All trips opened in the system (excluding draft) with their status — filter by agent, unit, dates, destination, project. A key day-to-day report for coordinators and finance.
- **1008 — Travel Status with Amounts**: Travel Status plus amounts.
- **1009 — Travel Status Angola**: Travel Status for flights to Angola only.
- **1011 — Travel Status Approver Manager**: Travel Status plus the approving manager (filter only).
- **1007 — No. of Trips by Day of Week**: Issued travels by departure day of week (excludes cancelled) — helps control travel patterns.
- **1012 — Refund**: Split into flights, hotels, cars; shows all refund requests to the agent and refunds approved/received, with vendor, employee, travel no., ticketing date, segment status and cancellation cause.
- **1013 — Trip Summarizing**: All trips except Draft; data from approved/closed invoices, plus employee expenses/per-diem that entered after the expense report was sent for approval.
- **1016 — Suits Report**: Number of days each employee is entitled to a suit allowance (custom development).
- **2011 — Top Destinations**: Number of flight tickets per destination (country/city) with reservation counts, total and average cost — shows the most popular destinations, with a graph.`;
