import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient, withFetch } from '@angular/common/http';
import { HttpTransportType, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { REALTIME_CONNECTION, RealtimeConnection } from './api/realtime';

/**
 * The real SignalR adapter behind {@link REALTIME_CONNECTION}.
 *
 * `withAutomaticReconnect` handles the retry schedule; what it deliberately does not do is
 * tell the client what it missed, which is why `ForecourtRealtime` resynchronises against a
 * server snapshot on every reconnect rather than trusting the frames it happens to receive
 * next. WebSockets only: long-polling would mask exactly the disconnection behaviour this
 * dashboard is meant to demonstrate.
 */
function signalrConnection(): RealtimeConnection {
  return new HubConnectionBuilder()
    .withUrl('/hubs/forecourt', { transport: HttpTransportType.WebSockets, skipNegotiation: true })
    .withAutomaticReconnect([0, 1000, 2000, 5000, 10000])
    .configureLogging(LogLevel.Warning)
    .build();
}

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideZonelessChangeDetection(),
    provideHttpClient(withFetch()),
    { provide: REALTIME_CONNECTION, useValue: signalrConnection },
  ],
};
