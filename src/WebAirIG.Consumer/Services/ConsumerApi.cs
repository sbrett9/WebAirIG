using WebAirIG.Consumer.Services.Detection;

namespace WebAirIG.Consumer.Services;

/// <summary>Read-only JSON status for scripts and automated tests (the Blazor UI does not use it).</summary>
public static class ConsumerApi
{
    public static void MapConsumerApi(this WebApplication app)
    {
        app.MapGet("/api/stats", (IStreamReceiverService receiver, ICameraPipelineService pipelines, IDetectorService detector,
            IEvaluationService evaluation, IDatasetRecorderService recorder) => Results.Ok(new
        {
            Receiver = new { receiver.Status, receiver.Stats?.MessagesReceived, receiver.Stats?.BytesReceived, receiver.Stats?.FragmentsDropped, receiver.Stats?.ParseErrors },
            Decoder = pipelines.FfmpegStatus,
            Cameras = pipelines.GetStats(),
            Detector = new { detector.Status, detector.ModelInfo, detector.MeanInferenceMs, Latest = pipelines.CameraIds.Select(c => detector.Latest(c)).Where(r => r is not null).Select(r => new { r!.CameraId, r.Frame, Count = r.Detections.Count, r.InferenceMs }) },
            Evaluation = evaluation.CameraRows(),
            Recorder = new { recorder.Recording, recorder.ImagesWritten, recorder.ObjectsWritten, recorder.DatasetPath },
        }));
    }
}
